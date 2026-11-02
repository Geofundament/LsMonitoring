// Источник: GfDistribution/client/GfLicense.cs. В программах лежит копия этого файла:
// правьте источник, затем GfDistribution/scripts/sync-client.ps1 разносит его по программам.
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Geofundament.Distribution;

public enum GfEdition
{
    Demo,
    Full,
}

/// <summary>Что программа знает о своей лицензии сейчас.</summary>
public sealed record GfLicenseState(GfEdition Edition, string Holder, DateTimeOffset? ExpiresAt, string Message)
{
    public static readonly GfLicenseState Demo = new(GfEdition.Demo, "", null, "");

    public bool IsFull => Edition == GfEdition.Full;
}

/// <param name="Url">Установщик на GitHub; без адреса обновление скачивается через сервис.</param>
public sealed record GfUpdate(string Version, string Notes, string? Url = null);

/// <summary>
/// Лицензия и обновления программы через сервис выдачи.
/// Ключ записывает установщик или вводит пользователь; сервис отвечает подписанным разрешением на 14 дней
/// для этого компьютера. Без ключа, без разрешения или с отказом сервиса программа работает как демо.
/// Это защита от простого копирования, а не от взлома.
/// </summary>
public sealed class GfLicense
{
    /// <summary>
    /// Адреса сервиса по очереди. Свой адрес первым: пока записи dist нет в DNS, запрос сразу проваливается и
    /// идёт на workers.dev. Переезд сервиса — запись в DNS geofundament.ru, без нового выпуска.
    /// GF_DIST_SERVICE заменяет оба адреса: так проверяют сервис на новом месте до переключения DNS.
    /// </summary>
    public static readonly IReadOnlyList<string> Services = Environment.GetEnvironmentVariable("GF_DIST_SERVICE")
        is { Length: > 0 } custom
        ? [custom.TrimEnd('/')]
        : ["https://dist.geofundament.ru", "https://gf-dist.gf-dist.workers.dev"];

    // Открытый ключ подписи разрешений (SubjectPublicKeyInfo, Base64). Закрытый хранится только в сервисе.
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExHxW/G+aL/VXC6CyqT4OoeyzAUX1yDx3chVlvNrsrzWun6iCVVbbZ23ldkNKpVUsMAuEfSsBRweOuwzeenTptg==";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _app;
    private readonly string _title;
    private readonly string _version;
    private readonly string _folder;
    private readonly string _publicKey;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly IReadOnlyList<string> _services;
    private string _service;

    /// <param name="app">Идентификатор в сервисе: osadka, hordeform, nivtropy.</param>
    /// <param name="title">Название для папки данных и файла установщика: Osadka, HorDeform, Nivtropy.</param>
    public GfLicense(
        string app,
        string title,
        string version,
        string? folder = null,
        HttpClient? http = null,
        Func<DateTimeOffset>? now = null,
        string? publicKey = null,
        IReadOnlyList<string>? services = null
    )
    {
        _services = services ?? Services;
        _service = _services[^1];
        _app = app;
        _title = title;
        _version = version;
        _folder =
            folder
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Геофундамент",
                title
            );
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _publicKey = publicKey ?? PublicKey;
    }

    public GfLicenseState State { get; private set; } = GfLicenseState.Demo;

    public GfUpdate? Update { get; private set; }

    /// <summary>Срабатывает после проверки у сервиса, если изменилось состояние или появилось обновление.</summary>
    public event Action? Changed;

    /// <summary>Ключ из личной ссылки; пусто — демо.</summary>
    public string Key
    {
        get
        {
            var path = Path.Combine(_folder, "key");
            return File.Exists(path) ? File.ReadAllText(path).Trim().ToLowerInvariant() : "";
        }
    }

    /// <summary>
    /// Ключ, введённый в программе, — тот же, что в личной ссылке. Полная версия включается после проверки у
    /// сервиса (<see cref="CheckAsync"/>) и перезапуска.
    /// </summary>
    public void SetKey(string key)
    {
        var clean = key.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(clean, "^[a-z2-7]{16}$"))
            throw new ArgumentException("Ключ — 16 латинских букв и цифр из письма или со страницы программ.");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "key"), clean);
    }

    public static string MachineId { get; } = ComputeMachineId();

    private string LeasePath => Path.Combine(_folder, "lease.json");

    private string DemoPath => Path.Combine(_folder, "demo.json");

    /// <summary>
    /// Переключатели демо из панели сервиса, проверенные по подписи; null — работают встроенные в программу.
    /// Читаются при запуске: набор, полученный при проверке, действует со следующего запуска.
    /// </summary>
    public JsonElement? DemoSettings { get; private set; }

    /// <summary>Быстрый старт без сети: действующее сохранённое разрешение включает полную версию.</summary>
    public GfLicenseState LoadSaved()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SignedLease>(File.ReadAllText(LeasePath), Json);
            State = saved is null
                ? GfLicenseState.Demo
                : Verify(saved.Payload, saved.Signature, MachineId, _app, _now(), _publicKey) ?? GfLicenseState.Demo;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            State = GfLicenseState.Demo;
        }
        try
        {
            var demo = JsonSerializer.Deserialize<SignedLease>(File.ReadAllText(DemoPath), Json);
            DemoSettings = demo is null ? null : VerifyDemo(demo.Payload, demo.Signature, _app, _publicKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DemoSettings = null;
        }
        return State;
    }

    /// <summary>
    /// Проверка у сервиса; false — ни один адрес не ответил. Без ответа состояние остаётся прежним.
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken cancel = default)
    {
        var reply = await AskAsync(cancel);
        if (reply is null)
            return false;

        Update = reply.Update is { } update ? new GfUpdate(update.Version, update.Notes, update.Url) : null;
        var full = reply.Lease is { } lease
            ? Verify(lease.Payload, lease.Signature, MachineId, _app, _now(), _publicKey)
            : null;
        if (reply.Edition == "full" && full is not null)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(LeasePath, JsonSerializer.Serialize(reply.Lease, Json));
            State = full;
        }
        else if (reply.Edition == "demo")
        {
            if (File.Exists(LeasePath))
                File.Delete(LeasePath);
            State = GfLicenseState.Demo with { Message = reply.Message };
        }
        if (reply.Demo is null)
        {
            if (File.Exists(DemoPath))
                File.Delete(DemoPath);
        }
        else if (VerifyDemo(reply.Demo.Payload, reply.Demo.Signature, _app, _publicKey) is not null)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(DemoPath, JsonSerializer.Serialize(reply.Demo, Json));
        }
        Changed?.Invoke();
        return true;
    }

    // Адреса по очереди: сбой, чужая страница или отказ одного адреса ведут к следующему.
    private async Task<CheckReply?> AskAsync(CancellationToken cancel)
    {
        var request = new
        {
            app = _app,
            key = Key,
            machineId = MachineId,
            version = _version,
        };
        foreach (var service in _services)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync($"{service}/v1/check", request, Json, cancel);
                if (!response.IsSuccessStatusCode)
                    continue;
                var reply = await response.Content.ReadFromJsonAsync<CheckReply>(Json, cancel);
                if (reply is { Ok: true })
                {
                    _service = service;
                    return reply;
                }
            }
            catch (Exception ex)
                when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
            {
                if (cancel.IsCancellationRequested)
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Скачивает установщик текущей версии и запускает его без вопросов. После этого программа должна закрыться:
    /// установщик заменит файлы и запустит её снова. Файл скачивает сама программа, поэтому SmartScreen не спрашивает.
    /// </summary>
    public async Task InstallUpdateAsync(CancellationToken cancel = default)
    {
        if (Update is null)
            throw new InvalidOperationException("Обновления нет.");
        var key = Key;
        var url = Update.Url ?? $"{_service}/get/{_app}" + (key.Length > 0 ? $"?t={key}" : "");
        var file = Path.Combine(Path.GetTempPath(), $"{_title}-Setup-{Update.Version}.exe");
        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(file);
            await response.Content.CopyToAsync(output, cancel);
        }
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }

    /// <summary>Проверяет подпись и условия разрешения; null — разрешение не годится.</summary>
    public static GfLicenseState? Verify(
        string payload,
        string signature,
        string machineId,
        string app,
        DateTimeOffset now,
        string publicKey
    )
    {
        try
        {
            var lease = Signed(payload, signature, publicKey) is { } bytes
                ? JsonSerializer.Deserialize<Lease>(bytes, Json)
                : null;
            if (lease is not { V: 1 } || lease.App != app || lease.MachineId != machineId || lease.LeaseUntil <= now)
                return null;
            return new GfLicenseState(GfEdition.Full, lease.Holder, lease.ExpiresAt, "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Проверяет подпись набора демо и программу; null — набор не годится.</summary>
    public static JsonElement? VerifyDemo(string payload, string signature, string app, string publicKey)
    {
        try
        {
            var demo = Signed(payload, signature, publicKey) is { } bytes
                ? JsonSerializer.Deserialize<DemoSet>(bytes, Json)
                : null;
            if (
                demo is not { V: 1, Kind: "demo" }
                || demo.App != app
                || demo.Settings.ValueKind != JsonValueKind.Object
            )
                return null;
            return demo.Settings.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Содержимое, если подпись сходится с открытым ключом сервиса.
    private static byte[]? Signed(string payload, string signature, string publicKey)
    {
        try
        {
            var bytes = FromBase64Url(payload);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(bytes, FromBase64Url(signature), HashAlgorithmName.SHA256) ? bytes : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    // SHA-256 от MachineGuid: компьютер узнаётся, а его имя и сам идентификатор в сервис не уходят.
    private static string ComputeMachineId()
    {
        string? guid = null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Без доступа к реестру остаётся имя компьютера: хуже, но стабильно.
        }
        var source = "geofundament|" + (guid ?? Environment.MachineName);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static byte[] FromBase64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }

    private sealed record SignedLease(string Payload, string Signature);

    private sealed record Lease(
        int V,
        string App,
        string Holder,
        string MachineId,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset LeaseUntil
    );

    private sealed record DemoSet(int V, string Kind, string App, JsonElement Settings);

    private sealed record UpdateReply(string Version, string Notes, string? Url);

    private sealed record CheckReply(
        bool Ok,
        string Edition,
        string Message,
        SignedLease? Lease,
        UpdateReply? Update,
        SignedLease? Demo
    );
}

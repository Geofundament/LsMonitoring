using LsMonitoring.Core.Configuration;
using LsMonitoring.Core.IO;

namespace LsMonitoring.Core.Alarms;

public sealed class ActiveEmailAlarm
{
    public DateTime StartTime { get; set; }
    public double LastValue { get; set; }
}

public sealed class EmailAlertService : IAlertChannel
{
    private static readonly string LogFilePath = AppPaths.LogFile("email_debug.txt");

    private readonly EmailConfig _config;
    private readonly IEmailSender _sender;
    private readonly Dictionary<(int NodeId, string Axis), ActiveEmailAlarm> _activeAlarms = [];
    private readonly SemaphoreSlim _alarmGate = new(1, 1);

    public string? LastError { get; private set; }
    public string ChannelName => "Email";

    /// <param name="previous">
    /// The instance this one replaces after a settings change: its open alarms carry over, so the "норма" email
    /// still goes out for an alarm that started before the change.
    /// </param>
    public EmailAlertService(EmailConfig config, IEmailSender? sender = null, EmailAlertService? previous = null)
    {
        _config = config;
        _sender = sender ?? new SmtpEmailSender();
        if (previous is not null)
        {
            foreach (var (key, alarm) in previous._activeAlarms)
            {
                _activeAlarms[key] = alarm;
            }
        }
    }

    public async Task<bool> SendTestMessageAsync()
    {
        return await SendEmailAsync(
            "ГФ 402: тестовое письмо",
            "Тестовое письмо от ГФ 402.\n\nЕсли вы видите это сообщение, почтовые уведомления настроены.");
    }

    public Task<bool> SendTestAsync()
    {
        return SendTestMessageAsync();
    }

    public Task NotifyStartedAsync(AlertEvent alertEvent)
    {
        return UpdateAlarmAsync(alertEvent.NodeId, alertEvent.Axis, true, alertEvent.CurrentValue, alertEvent.StartedAt);
    }

    public Task NotifyResolvedAsync(AlertEvent alertEvent)
    {
        return UpdateAlarmAsync(
            alertEvent.NodeId,
            alertEvent.Axis,
            false,
            alertEvent.CurrentValue,
            alertEvent.ResolvedAt ?? alertEvent.UpdatedAt);
    }

    public async Task UpdateAlarmAsync(int nodeId, string axis, bool isCritical, double value, DateTime timestamp)
    {
        await _alarmGate.WaitAsync();
        try
        {
            if (!_config.Enabled)
            {
                return;
            }

            var key = (nodeId, axis);

            if (isCritical)
            {
                if (_activeAlarms.TryGetValue(key, out var active))
                {
                    active.LastValue = value;
                    return;
                }

                var sent = await SendEmailAsync(
                    $"ГФ 402: тревога, узел {nodeId}, ось {axis}",
                    FormatAlarmMessage(nodeId, axis, value, timestamp));

                if (sent)
                {
                    _activeAlarms[key] = new ActiveEmailAlarm
                    {
                        StartTime = timestamp,
                        LastValue = value
                    };
                }

                return;
            }

            if (_activeAlarms.TryGetValue(key, out var alarm))
            {
                if (!_config.SendResolvedNotifications)
                {
                    _activeAlarms.Remove(key);
                    return;
                }

                var sent = await SendEmailAsync(
                    $"ГФ 402: норма, узел {nodeId}, ось {axis}",
                    FormatResolvedMessage(nodeId, axis, alarm.StartTime, timestamp));

                if (sent)
                {
                    _activeAlarms.Remove(key);
                }
            }
        }
        finally
        {
            _alarmGate.Release();
        }
    }

    private async Task<bool> SendEmailAsync(string subject, string body)
    {
        LastError = null;

        var transport = _config.ResolveTransport();
        if (transport is null)
        {
            RecordError(_config.UsesService
                ? "Сервисная почта не настроена в этой сборке."
                : "Не задан альтернативный SMTP: нужны отправитель и сервер.");
            return false;
        }

        var recipients = _config.Recipients
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            RecordError("Не указаны корректные получатели email.");
            return false;
        }

        var result = await _sender.SendAsync(transport, subject, body, recipients);
        if (result.Success)
        {
            LogDiagnostic($"email-sent host={transport.Host}:{transport.Port} recipients={recipients.Count} subject={subject}");
            return true;
        }

        RecordError($"smtp exception: {result.Error}");
        return false;
    }

    private static string FormatAlarmMessage(int nodeId, string axis, double value, DateTime startTime)
    {
        return
            "ГФ 402\n\n" +
            $"Узел: {nodeId}\n" +
            $"Ось: {axis}\n" +
            "Статус: критическое отклонение\n" +
            $"Отклонение: {value:F3}°\n" +
            $"Начало: {startTime:dd.MM.yyyy HH:mm:ss}";
    }

    private static string FormatResolvedMessage(int nodeId, string axis, DateTime startTime, DateTime resolveTime)
    {
        var duration = resolveTime - startTime;
        return
            "ГФ 402\n\n" +
            $"Узел: {nodeId}\n" +
            $"Ось: {axis}\n" +
            "Статус: значение вернулось в норму\n" +
            $"Начало события: {startTime:dd.MM.yyyy HH:mm:ss}\n" +
            $"Завершено: {resolveTime:dd.MM.yyyy HH:mm:ss}\n" +
            $"Длительность: {duration:hh\\:mm\\:ss}";
    }

    private void RecordError(string message)
    {
        LastError = message;
        LogDiagnostic(message);
    }

    public static void LogDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTime.Now}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not break alert delivery.
        }
    }

}

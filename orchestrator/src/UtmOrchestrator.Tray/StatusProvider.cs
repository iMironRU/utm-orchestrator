using System.Net.Http;
using System.Text.Json;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Tray;

public enum OverallStatus { Ok, Warn, Fault, Unknown, Starting }

public enum RowKind { Ok, Starting, Fault, Stopped, Unknown }

/// <summary>Строка списка УТМ для трея (уже готовая к показу, без деталей Core).</summary>
public sealed record UtmRow(string Name, int Port, RowKind Kind, string StateText, string? Detail);

public sealed record StatusSnapshot(
    OverallStatus Overall,
    string Summary,
    bool BringUp,
    ServiceState OrchestratorService,
    IReadOnlyList<UtmRow> Utms);

/// <summary>
/// Сводный статус для трея — берётся из ЕДИНОГО источника: локальной панели службы
/// (GET /api/status). Трей больше НЕ делает свою discovery/health — иначе во время
/// boot-подъёма он и служба одновременно лезли бы к PC/SC, а неподнятые УТМ мигали
/// «Сбоем». Теперь: пока служба сообщает bringUp=true, неготовые УТМ показываются как
/// «Запускается…». Состояние самой службы читаем локально (работает, даже если HTTP
/// ещё не отвечает). Read-only.
/// </summary>
public static class StatusProvider
{
    public const string OrchestratorServiceName = "UtmOrchestrator";
    private const string StatusUrl = "http://127.0.0.1:8090/api/status";

    // UseProxy=false + 127.0.0.1: иначе унаследованный HTTP_PROXY гонит localhost через
    // прокси и каждый опрос висит секундами (проверено на службе).
    private static readonly HttpClient _http = new(
        new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<StatusSnapshot> GetAsync(CancellationToken ct = default)
    {
        ServiceState svc = ServiceControl.GetState(OrchestratorServiceName);

        string? json = null;
        try { json = await _http.GetStringAsync(StatusUrl, ct); }
        catch { /* панель ещё не готова или служба стоит — обработаем ниже */ }

        if (json is null)
        {
            // Служба работает, но HTTP молчит — панель ещё поднимается; иначе служба стоит.
            bool starting = svc == ServiceState.Running || svc == ServiceState.StartPending;
            return new StatusSnapshot(
                starting ? OverallStatus.Starting : OverallStatus.Unknown,
                starting ? "запускается, идёт подъём УТМ (до ~минуты)…" : "служба не запущена",
                starting, svc, Array.Empty<UtmRow>());
        }

        StatusDto? dto;
        try { dto = JsonSerializer.Deserialize<StatusDto>(json, JsonOpts); }
        catch { dto = null; }

        if (dto is null)
            return new StatusSnapshot(OverallStatus.Unknown, "нет данных", false, svc, Array.Empty<UtmRow>());

        bool bringUp = dto.bringUp;

        // Живой прогресс подъёма (быстрый путь /api/status) — показываем «подъём N/M ·
        // готово через ~M:SS» с отсчётом, вместо безликого «запускается…».
        if (bringUp && dto.boot is { } b && b.active)
        {
            string eta = b.etaRemainingSeconds is int r && r > 0 ? $" · готово через ~{FmtDuration(r)}"
                       : b.etaRemainingSeconds is 0 ? " · почти готово"
                       : "…";
            var brows = (dto.instances ?? new List<InstanceDto>()).Select(i =>
            {
                bool up = string.Equals(i.verdict, "Ok", StringComparison.OrdinalIgnoreCase);
                string nm = !string.IsNullOrWhiteSpace(i.title) ? i.title! : (i.service ?? "УТМ");
                return new UtmRow(nm, i.port, up ? RowKind.Ok : RowKind.Starting, up ? "Работает" : "Запускается…", null);
            }).ToList();
            return new StatusSnapshot(OverallStatus.Starting,
                $"подъём {b.ready}/{b.total}{eta}", true, svc, brows);
        }

        var rows = new List<UtmRow>();
        foreach (var i in dto.instances ?? new List<InstanceDto>())
        {
            RowKind kind = i.verdict switch
            {
                "Ok" => RowKind.Ok,
                "Faulty" => RowKind.Fault,
                "Stopped" => RowKind.Stopped,
                _ => RowKind.Unknown,
            };
            // Во время подъёма любое «не в норме» — это «ещё запускается», а не поломка.
            if (bringUp && kind != RowKind.Ok) kind = RowKind.Starting;

            string state = kind switch
            {
                RowKind.Ok => "Работает",
                RowKind.Starting => "Запускается…",
                RowKind.Fault => "Сбой",
                RowKind.Stopped => "Остановлен",
                _ => "—",
            };
            string name = !string.IsNullOrWhiteSpace(i.title) ? i.title!
                        : !string.IsNullOrWhiteSpace(i.service) ? i.service!
                        : "УТМ";
            rows.Add(new UtmRow(name, i.port, kind, state, string.IsNullOrWhiteSpace(i.reason) ? null : i.reason));
        }

        int total = dto.total;
        int ok = dto.ok;
        OverallStatus overall;
        string summary;

        if (bringUp)
        {
            overall = OverallStatus.Starting;
            summary = total > 0 ? $"идёт подъём… {ok}/{total} готово" : "идёт подъём…";
        }
        else if (total == 0)
        {
            overall = OverallStatus.Unknown;
            summary = "УТМ не найдены";
        }
        else if (dto.faulty > 0)
        {
            overall = OverallStatus.Fault;
            summary = $"{ok}/{total} в норме, сбоев: {dto.faulty}";
        }
        else
        {
            overall = OverallStatus.Ok;
            summary = $"{ok}/{total} в норме";
        }

        return new StatusSnapshot(overall, summary, bringUp, svc, rows);
    }

    private static string FmtDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}с";
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Разбираем ровно те поля /api/status, что нужны трею.
    private sealed class StatusDto
    {
        public int total { get; set; }
        public int ok { get; set; }
        public int faulty { get; set; }
        public bool bringUp { get; set; }
        public BootDto? boot { get; set; }
        public List<InstanceDto>? instances { get; set; }
    }

    private sealed class BootDto
    {
        public bool active { get; set; }
        public int ready { get; set; }
        public int total { get; set; }
        public string? phase { get; set; }
        public int elapsedSeconds { get; set; }
        public int? etaRemainingSeconds { get; set; }
    }

    private sealed class InstanceDto
    {
        public string? service { get; set; }
        public int port { get; set; }
        public string? verdict { get; set; }
        public string? reason { get; set; }
        public string? title { get; set; }
    }
}

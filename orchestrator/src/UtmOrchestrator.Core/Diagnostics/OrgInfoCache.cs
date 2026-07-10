using System.Collections.Concurrent;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Кэш орг-данных УТМ (организация/адрес из сертификата) по ФСРАР-ID. Данные
/// сертификата статичны, меняются только при перепривязке токена, поэтому в
/// горячем пути опроса статуса их не нужно перечитывать по HTTP каждый раз.
/// Кэшируются только успешные ответы; при неудаче попробуем снова в следующий раз.
/// Потокобезопасно (in-memory, живёт время работы процесса).
/// </summary>
public sealed class OrgInfoCache
{
    private readonly ConcurrentDictionary<string, UtmOrgInfo> _byFsrar =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string fsrar, out UtmOrgInfo info) => _byFsrar.TryGetValue(fsrar, out info!);

    public void Set(string fsrar, UtmOrgInfo info)
    {
        if (!string.IsNullOrEmpty(fsrar)) _byFsrar[fsrar] = info;
    }

    /// <summary>Сбросить кэш (например, после перепривязки токена).</summary>
    public void Clear() => _byFsrar.Clear();
}

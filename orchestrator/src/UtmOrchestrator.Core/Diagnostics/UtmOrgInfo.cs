namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Данные организации/владельца УТМ из сертификата (эндпоинты /api/rsa/orginfo и
/// /api/gost/orginfo). Для показа «адреса/названия точки» в интерфейсе.
/// </summary>
public sealed record UtmOrgInfo(
    string? Organization,   // rsa.o — организация (у ИП = ФИО, у ООО = название)
    string? Inn,            // rsa.ou — ИНН
    string? PersonName,     // gost.cn — ФИО владельца ключа (нормальный регистр)
    string? Address)        // собрано из gost street/l/st, если есть
{
    /// <summary>
    /// Строка для отображения по умолчанию (если не задано кастомное краткое имя):
    /// адрес → организация → ФИО. Может быть null, если данных нет.
    /// </summary>
    public string? Display => FirstNonEmpty(Address, Organization, PersonName);

    private static string? FirstNonEmpty(params string?[] xs)
    {
        foreach (var x in xs)
            if (!string.IsNullOrWhiteSpace(x)) return x!.Trim();
        return null;
    }
}

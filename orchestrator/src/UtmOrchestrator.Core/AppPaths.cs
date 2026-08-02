namespace UtmOrchestrator.Core;

/// <summary>
/// Единые пути установки. Раскладка (с релиза «bin/data/utms/cache/transfer»):
/// <code>
/// C:\UtmOrchestrator\
///   bin\app\      — наш код (exe/dll/wwwroot/tools)   ← AppContext.BaseDirectory
///   bin\runtime\  — приватный .NET
///   data\         — state.json, names.json, logs\
///   utms\         — utm-1, utm-2, …
///   cache\        — шаблон дистрибутива УТМ (utm-dist)
///   transfer\     — exports\ + imports\ (бандлы переноса)
/// </code>
/// Данные/УТМ живут в КОРНЕ установки (не рядом с exe), поэтому апдейт меняет только
/// bin\, а data/utms/cache/transfer не трогает. В деве/плоской раскладке (exe не в
/// bin\app) корень = каталог exe — всё работает как раньше.
/// Служба (LocalSystem), CLI (admin) и трей (пользователь) стартуют из одной bin\app,
/// поэтому корень у всех общий.
/// </summary>
public static class AppPaths
{
    /// <summary>Корень установки: при боевой раскладке — на два уровня выше bin\app; иначе каталог exe.</summary>
    public static string Root { get; } = ComputeRoot();

    private static string ComputeRoot()
    {
        string bd = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        // ...\bin\app → корень на два каталога выше (bin\app → bin → корень).
        string tail = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar + "app";
        if (bd.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
        {
            var bin = Path.GetDirectoryName(bd);              // ...\bin
            var root = bin is null ? null : Path.GetDirectoryName(bin); // ...\
            if (!string.IsNullOrEmpty(root)) return root!;
        }
        return bd; // плоская/дев-раскладка — корень рядом с exe
    }

    /// <summary>Данные: &lt;root&gt;\data.</summary>
    public static string DataDir { get; } = Init(Path.Combine(Root, "data"));

    /// <summary>Логи: &lt;root&gt;\data\logs.</summary>
    public static string LogsDir { get; } = Init(Path.Combine(DataDir, "logs"));

    /// <summary>Корень папок УТМ, которыми управляем МЫ: &lt;root&gt;\utms. Новые УТМ (add-all/импорт)
    /// кладём сюда — «наши» в одном месте, отделены от чужих, одинаково на любой машине.</summary>
    public static string UtmRoot { get; } = Init(Path.Combine(Root, "utms"));

    /// <summary>Кэш шаблона дистрибутива УТМ: &lt;root&gt;\cache.</summary>
    public static string CacheDir { get; } = Init(Path.Combine(Root, "cache"));

    /// <summary>Бандлы переноса: &lt;root&gt;\transfer (exports/imports внутри).</summary>
    public static string TransferDir { get; } = Init(Path.Combine(Root, "transfer"));

    /// <summary>Следующая свободная папка для нового УТМ под нашим корнем: &lt;utms&gt;\utm-1, utm-2, …</summary>
    public static string NextUtmFolder()
    {
        int i = 1;
        string f;
        do { f = Path.Combine(UtmRoot, "utm-" + i); i++; } while (Directory.Exists(f));
        return f;
    }

    /// <summary>Файл данных по имени (state.json, serials.json, …).</summary>
    public static string Data(string name) => Path.Combine(DataDir, name);

    /// <summary>Файл лога по имени.</summary>
    public static string Log(string name) => Path.Combine(LogsDir, name);

    /// <summary>Папка внутри transfer\ (exports/imports).</summary>
    public static string Transfer(string sub) => Init(Path.Combine(TransferDir, sub));

    /// <summary>Основной журнал операций.</summary>
    public static string BringupLog => Log("bringup.log");

    private static string Init(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* создастся при первой записи */ }
        return dir;
    }
}

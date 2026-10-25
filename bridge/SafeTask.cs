namespace OtzariaBridge;

/// <summary>
/// #134–#135: משימות "שגר ושכח" (Task.Run / ReadToEndAsync בלי await) שנכשלו לא נצפו,
/// ובגמר איסוף הזבל התקבל System.AggregateException ("A Task's exception(s) were not observed").
/// כאן כל משימה כזו מקבלת המשך שצופה בחריגה ורושם אותה ליומן — במקום דיווח task-error אוטומטי.
/// </summary>
internal static class SafeTask
{
    /// <summary>מריץ פעולה ברקע; חריגה נרשמת ליומן ואינה נשארת לא-נצפית</summary>
    public static Task Run(Action action, string where = null) =>
        Task.Run(action).Observe(where ?? action.Method.Name);

    /// <summary>מריץ פעולה אסינכרונית ברקע; חריגה נרשמת ליומן ואינה נשארת לא-נצפית</summary>
    public static Task Run(Func<Task> action, string where = null) =>
        Task.Run(action).Observe(where ?? action.Method.Name);

    /// <summary>מסמן משימה כנצפית: אם תיכשל, החריגה תירשם ליומן (שקט = רק נצפית, בלי רישום)</summary>
    public static T Observe<T>(this T task, string where, bool quiet = false) where T : Task
    {
        if (task == null) return null;
        task.ContinueWith(t =>
        {
            var ex = t.Exception?.GetBaseException(); // הגישה ל-Exception מסמנת את המשימה כנצפית
            if (quiet || ex == null) return;
            try { Config.Log("task " + (where ?? "?") + ": " + ex.GetType().Name + ": " + ex.Message); } catch { }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}

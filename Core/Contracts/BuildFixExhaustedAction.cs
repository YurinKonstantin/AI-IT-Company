namespace Core.Contracts;

/// <summary>Выбор пользователя, когда авто-фикс сборки исчерпан.</summary>
public enum BuildFixUserChoice
{
    Rollback = 0,
    ManualContinue = 1,
    ProceedWithError = 2
}

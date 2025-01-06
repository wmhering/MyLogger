namespace MyLogger.Common
{
    public interface IApplicationContext
    {
        string UserID { get; }
        string ThreadID { get; }
    }
}

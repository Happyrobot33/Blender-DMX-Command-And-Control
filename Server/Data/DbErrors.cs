using MySqlConnector;

namespace Server.Data;

public static class DbErrors
{
    public static bool IsDuplicateKey(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
                return true;
        }
        return false;
    }
}

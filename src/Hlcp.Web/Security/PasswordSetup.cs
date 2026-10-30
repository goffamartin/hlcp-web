namespace Hlcp.Web.Security;

public static class PasswordSetup
{
    public static async Task<int> RunAsync(AdminCredentialFile credential)
    {
        if (Console.IsInputRedirected) { Console.Error.WriteLine("Use --set-admin-password in an interactive terminal; password input is hidden."); return 2; }
        Console.Write("New admin password (at least 16 characters): ");
        var password = ReadPassword();
        Console.Write("Repeat password: ");
        if (password != ReadPassword() || password.Length is < 16 or > 1024)
        { Console.Error.WriteLine("Passwords must match and contain 16–1024 characters. Nothing changed."); return 2; }
        try { await credential.WriteAsync(password); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Password file could not be saved. Inspect local storage permissions."); return 1; }
        Console.WriteLine("Admin password saved as a hash. Existing sessions will be refused on their next validation. Controller credentials were not changed.");
        return 0;
    }

    private static string ReadPassword()
    {
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
            else if (!char.IsControl(key.KeyChar) && value.Length < 1025) value.Append(key.KeyChar);
        }
    }
}

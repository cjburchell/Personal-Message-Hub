using MessageHub.Web;

try
{
    var options = WebOptions.FromEnvironment();
    if (args is ["--set-password"])
    {
        var password = await WebPassword.ReadHiddenAsync("New local web password (12+ characters, hidden): ");
        var confirmation = await WebPassword.ReadHiddenAsync("Confirm password: ");
        if (password != confirmation) throw new ArgumentException("Passwords differ.");
        await new WebPassword(options.PasswordFile).SetAsync(password);
        Console.WriteLine("Local password hash saved. Restart the web app to invalidate existing sessions.");
        return;
    }
    if (args.Length > 0) throw new ArgumentException("Use --set-password or no arguments.");
    await using var app = await WebBootstrap.CreateAsync(options);
    Console.WriteLine("Personal Message Hub local chat: " + options.Origin + " (password required)");
    await app.RunAsync();
}
catch (Exception)
{
    Console.Error.WriteLine("Local web app could not start. Set a password with --set-password, then check local configuration, permissions and port availability.");
    Environment.ExitCode = 1;
}

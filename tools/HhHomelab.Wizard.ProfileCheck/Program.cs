using HhHomelab.Wizard.Infrastructure;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ProfileCheck <research-profile.json> [upstream-checkout]");
    return 2;
}
try
{
    var profile = ProfileReader.Parse(await File.ReadAllTextAsync(args[0]));
    Console.WriteLine($"{profile.Id}: {profile.Services.Length} services, {profile.Artifacts.Length} artifacts, {profile.Coverage.Length} steps.");
    Console.WriteLine("Research inventory only; deployment is not implemented.");
    if (args.Length == 2)
    {
        var failures = await ProfileReader.VerifySourceAsync(profile, args[1]);
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        if (failures.Count > 0) return 1;
        Console.WriteLine("Source content matches canonical UTF-8/LF fingerprints. Git identity and runtime compatibility are not asserted.");
    }
    return 0;
}
catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Profile/source verification failed. Check format, content and file access.");
    return 1;
}

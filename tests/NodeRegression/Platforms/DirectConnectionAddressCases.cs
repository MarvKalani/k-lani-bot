using KLaniBot.Platforms.Linux;

// The address an application announces for its own server comes from the application. Only unix sockets are used.
internal static class DirectConnectionAddressCases
{
    public static int Run()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }
        var checks = new Checks("Direct connection address");

        checks.That(DirectConnectionAddress.IsAccepted("unix:path=/tmp/runtime/at-spi2-DPWVW3/socket"), "a path socket is accepted");
        checks.That(
            DirectConnectionAddress.IsAccepted("unix:path=/tmp/dbus-3fgKwO5JyR,guid=0123abcd"),
            "a path socket with a guid is accepted");
        checks.That(DirectConnectionAddress.IsAccepted("unix:abstract=/tmp/dbus-3fgKwO5JyR"), "an abstract socket is accepted");
        checks.That(!DirectConnectionAddress.IsAccepted(null), "no address is refused");
        checks.That(!DirectConnectionAddress.IsAccepted(""), "an empty address is refused");
        checks.That(
            !DirectConnectionAddress.IsAccepted("unixexec:path=/bin/sh,argv1=-c"),
            "a transport that starts a process is refused");
        checks.That(!DirectConnectionAddress.IsAccepted("tcp:host=127.0.0.1,port=1234"), "a network transport is refused");
        checks.That(
            !DirectConnectionAddress.IsAccepted("unix:path=/tmp/a;unixexec:path=/bin/sh"),
            "an address list is refused even when it starts with a unix socket");

        return checks.Passed;
    }
}

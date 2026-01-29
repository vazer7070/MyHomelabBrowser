using System.Reflection;


namespace MyHomelabBrowser
{
    public static class AppVersion
    {
        public static string Current =>
        Assembly.GetExecutingAssembly()
        .GetName()
        .Version?
        .ToString(3) ?? "?";
    }
}
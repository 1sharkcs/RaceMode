// Replaced by the release workflow (.github/workflows/release.yml) from the git tag.
// A local build reports "local" and never checks for updates.
[assembly: System.Reflection.AssemblyVersion("0.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("local")]
[assembly: System.Reflection.AssemblyTitle("Race Mode")]
[assembly: System.Reflection.AssemblyProduct("Race Mode")]

namespace RaceMode
{
    static class BuildInfo
    {
        public const string Repo = "";   // "owner/name" on GitHub; set by the release workflow
    }
}

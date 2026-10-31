using HarmonyLib;

namespace Autom8er
{
    // Save logging rows and held logging outputs when the host saves its placed chests.
    [HarmonyPatch(typeof(SaveAndLoad), "SaveChests")]
    public static class LoggingRowSavePatch
    {
        static void Postfix()
        {
            VacuumCrateHelper.SaveLoggingRows();
        }
    }
}

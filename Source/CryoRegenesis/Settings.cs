using UnityEngine;
using Verse;

namespace BetterRimworlds.CryoRegenesis
{
    public class Settings: ModSettings
    {
        public int targetAge = 21;
        public bool regenUntilHealed = true;
        public bool healSimpleProsthetics = true;
        public bool healNotBad = false;

        public bool debugMode = false;

        override public void ExposeData()
        {
            Scribe_Values.Look(ref targetAge,             "brw.cryoregenesis.targetAge", 21);
            Scribe_Values.Look(ref regenUntilHealed,      "brw.cryoregenesis.regenUntilHealed", true);
            Scribe_Values.Look(ref healSimpleProsthetics, "brw.cryoregenesis.healSimpleProsthetics", true);
            Scribe_Values.Look(ref healNotBad,            "brw.cryoregenesis.healNotBad", false);
            Scribe_Values.Look(ref debugMode,             "brw.cryoregenesis.debugMode", false);
        }

        public void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing_Standard = new Listing_Standard();
            listing_Standard.Begin(inRect);

            string[] labels = {
                "Target age for regening Humans:",
                "Stop regenerating when a Human is fully healed? ",
                "Heal simple and wooden prosthetics?",
                "Heal non-bad body mods?",
                "Print debug messages?",
            };

            // targetAge = listing_Standard.TextEntryLabeled(labels[0], targetAge.ToString());
            string buffer = null;
            listing_Standard.TextFieldNumericLabeled<int>(labels[0], ref targetAge, ref buffer);

            listing_Standard.CheckboxLabeled(labels[1],  ref regenUntilHealed);
            listing_Standard.CheckboxLabeled(labels[2],  ref healSimpleProsthetics);
            listing_Standard.CheckboxLabeled(labels[3],  ref healNotBad);
            listing_Standard.CheckboxLabeled(labels[4],  ref debugMode);

            listing_Standard.End();

            CryoRegenesis.Settings.debugMode = debugMode;
        }
    }
}
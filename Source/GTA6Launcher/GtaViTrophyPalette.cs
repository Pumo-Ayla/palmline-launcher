// GTA VI PS5 trophy grades matched by achievement name to the local Xbox IDs.
internal static class GtaViTrophyPalette
{
    internal enum Grade { Bronze, Silver, Gold, Platinum }

    internal static Grade GradeOf(string id) {
        switch(id) {
            case "1000": return Grade.Platinum;
            case "6": case "13": return Grade.Gold;
            case "14": case "16": case "24": case "29": case "30": case "39":
                return Grade.Silver;
            default: return Grade.Bronze;
        }
    }

    internal static void ForAchievement(string id,out int metal,out int highlight) {
        switch(GradeOf(id)) {
            case Grade.Platinum: metal=0x91BDE6; highlight=0xE3F7FF; break;
            case Grade.Gold: metal=0xD6A342; highlight=0xFFF0B0; break;
            case Grade.Silver: metal=0xAAB4C4; highlight=0xF1F5FF; break;
            default: metal=0xC18662; highlight=0xFFD1AA; break;
        }
    }
}

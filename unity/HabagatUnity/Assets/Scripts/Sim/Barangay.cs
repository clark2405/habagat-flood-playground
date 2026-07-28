namespace Habagat
{
    public enum HouseStyle { Nipa, Townhouse, Store, Hall, Apartment }

    public struct House
    {
        public int X, Y;             // grid cell
        public HouseStyle Style;
        public House(int x, int y, HouseStyle s) { X = x; Y = y; Style = s; }
    }

    /// <summary>
    /// The buildings each preset starts with, mirroring PRESETS[...].houses in
    /// FloodPlayground.jsx.
    ///
    /// These are authored placements, not generated ones — the coastal barangay is
    /// deliberately two clusters either side of the river mouth, the river valley is
    /// two ribbons along the banks, and the urban preset is a grid. Reproducing the
    /// list exactly is what lets a Unity screenshot be compared against the web
    /// build's, and it also drives the yard dressing: fences and laundry lines are
    /// placed relative to houses rather than at random, because the point of them is
    /// to make the houses look lived in.
    /// </summary>
    public static class Barangay
    {
        public static House[] For(PresetType t)
        {
            switch (t)
            {
                case PresetType.River:
                    return new[]
                    {
                        new House(20, 15, HouseStyle.Townhouse), new House(25, 20, HouseStyle.Nipa),
                        new House(28, 28, HouseStyle.Store),     new House(30, 36, HouseStyle.Nipa),
                        new House(32, 44, HouseStyle.Hall),      new House(65, 18, HouseStyle.Townhouse),
                        new House(68, 26, HouseStyle.Nipa),      new House(70, 34, HouseStyle.Townhouse),
                        new House(72, 42, HouseStyle.Nipa),      new House(75, 50, HouseStyle.Store),
                        new House(15, 30, HouseStyle.Nipa),      new House(18, 42, HouseStyle.Townhouse),
                        new House(80, 22, HouseStyle.Nipa),      new House(82, 38, HouseStyle.Townhouse),
                    };

                case PresetType.Urban:
                    return new[]
                    {
                        new House(24, 20, HouseStyle.Apartment), new House(30, 20, HouseStyle.Apartment),
                        new House(36, 20, HouseStyle.Store),     new House(42, 20, HouseStyle.Hall),
                        new House(54, 20, HouseStyle.Apartment), new House(60, 20, HouseStyle.Apartment),
                        new House(66, 20, HouseStyle.Store),     new House(24, 30, HouseStyle.Apartment),
                        new House(30, 30, HouseStyle.Apartment), new House(36, 30, HouseStyle.Townhouse),
                        new House(54, 30, HouseStyle.Apartment), new House(60, 30, HouseStyle.Apartment),
                        new House(66, 30, HouseStyle.Townhouse), new House(24, 40, HouseStyle.Store),
                        new House(30, 40, HouseStyle.Townhouse), new House(36, 40, HouseStyle.Townhouse),
                        new House(54, 40, HouseStyle.Townhouse), new House(60, 40, HouseStyle.Store),
                        new House(66, 40, HouseStyle.Townhouse),
                    };

                case PresetType.Island:
                    return new[]
                    {
                        new House(38, 25, HouseStyle.Nipa),      new House(44, 22, HouseStyle.Townhouse),
                        new House(52, 22, HouseStyle.Hall),      new House(58, 25, HouseStyle.Nipa),
                        new House(32, 32, HouseStyle.Store),     new House(64, 32, HouseStyle.Nipa),
                        new House(34, 40, HouseStyle.Nipa),      new House(40, 44, HouseStyle.Townhouse),
                        new House(48, 46, HouseStyle.Townhouse), new House(56, 44, HouseStyle.Nipa),
                        new House(62, 40, HouseStyle.Store),     new House(48, 32, HouseStyle.Hall),
                    };

                default: // Coastal
                    return new[]
                    {
                        new House(18, 38, HouseStyle.Nipa),      new House(24, 36, HouseStyle.Nipa),
                        new House(30, 35, HouseStyle.Store),     new House(36, 37, HouseStyle.Townhouse),
                        new House(42, 40, HouseStyle.Hall),      new House(62, 38, HouseStyle.Nipa),
                        new House(68, 36, HouseStyle.Townhouse), new House(74, 35, HouseStyle.Nipa),
                        new House(80, 39, HouseStyle.Store),     new House(22, 28, HouseStyle.Townhouse),
                        new House(28, 26, HouseStyle.Townhouse), new House(34, 25, HouseStyle.Nipa),
                        new House(70, 27, HouseStyle.Nipa),      new House(76, 28, HouseStyle.Townhouse),
                    };
            }
        }
    }
}

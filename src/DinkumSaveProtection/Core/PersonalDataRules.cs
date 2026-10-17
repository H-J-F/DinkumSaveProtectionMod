using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DinkumSaveProtection.Core
{
    public static class PersonalDataRules
    {
        public static void Keys(Dictionary<string, object?> map, params string[] required)
        { if (map.Count != required.Length || map.Keys.Any(k => !required.Contains(k))) throw new InvalidDataException("Unexpected or missing personal fields"); }
        public static void Item(long id, long stack, int count, Func<int, bool> exists)
        { if (id < -1 || id >= count || stack < 0 || stack > int.MaxValue || id >= 0 && !exists((int)id)) throw new InvalidDataException("Unknown item or invalid stack"); }
        public static void Vitals(double stamina, double maximum, long maxHealth)
        { if (double.IsNaN(stamina) || double.IsInfinity(stamina) || double.IsNaN(maximum) || double.IsInfinity(maximum) || Math.Abs(stamina) > float.MaxValue || maximum > float.MaxValue || maximum <= 0 || maxHealth < 1 || maxHealth > int.MaxValue) throw new InvalidDataException("Invalid vitals"); }
    }
}

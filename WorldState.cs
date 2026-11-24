namespace CarriageTracker;

// Everything the mod knows about the objects of one server world (audit 07.11 by Nikolai, A3): caravans, routes and
// their patrols, starts and repairs, markers and the destroy queue, loot containers, the trial caravan. Each part
// keeps its own state class (WorldState.Of<T>), and all of them belong to the world: a new server world (Caravans.ServerWorld)
// drops them whole, with the queries made in the old one. Until 1.0.26 these were static collections, and on a
// world change only some of them were cleared by hand; an entity key (index, version) of the old world could match
// an unrelated object of the new one.
// What isn't the world's stays static: settings, counters since the server start, caches of game types and prefabs.
static class WorldState
{
    static int _generation = 1;

    // The current generation's state of one part; made anew, empty, after a reset.
    public static T Of<T>() where T : class, new() => Slot<T>.Get(_generation);

    public static void Reset()
    {
        _generation++;
        Queries.Reset();
    }

    static class Slot<T> where T : class, new()
    {
        static T _value;
        static int _of;

        public static T Get(int generation)
        {
            if (_of != generation || _value == null)
            {
                _value = new T();
                _of = generation;
            }
            return _value;
        }
    }
}

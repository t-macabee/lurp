namespace Lurp.Tests;

/// <summary>
///     One project with a partial type declared in two documents and an ordinary
///     type in a third; shared by the summary-counter and pin tests.
/// </summary>
internal static class PartialShapeFixture
{
    public const string ProjectName = "Core";

    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["PartialShape.Part1.cs"] = """
                                    namespace Core;

                                    public partial class PartialShape
                                    {
                                        public int FromPartOne() => 1;
                                    }
                                    """,
        ["PartialShape.Part2.cs"] = """
                                    namespace Core;

                                    public partial class PartialShape
                                    {
                                        public int FromPartTwo() => 2;
                                    }
                                    """,
        ["Widget.cs"] = """
                        namespace Core;

                        public class Widget
                        {
                            public int Value { get; set; }
                        }
                        """
    };
}

namespace CrossProject.Lib;

public static class PublicApi
{
    public static int SharedTarget() => 1;

#if NET10_0
    public static int OnlyNet10() => 10;
#endif

#if NET9_0
    public static int OnlyNet9() => 9;
#endif
}

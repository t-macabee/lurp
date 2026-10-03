namespace CrossProject.Lib;

public class ShapeRepo<T> where T : notnull
{
    public T Get(T value) => value;

    public class Nested<TOther>
    {
        public TOther Echo(TOther value) => value;
    }
}

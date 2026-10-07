namespace CallShapes.App;

public sealed class ShapeNestedHost
{
    public sealed class Level1
    {
        private readonly int _seed = CoreApi.NestedFieldInitializerTarget();

        public Level1() : this(CoreApi.NestedConstructorInitializerTarget())
        {
        }

        public Level1(int value) => Seed = value;

        public int Seed { get; }

        public int Run(ShapeNestedTarget target)
        {
            var created = new ShapeNestedCreated();
            target.Counter = target.Counter + 1;
            return CoreApi.NestedCallTarget() + created.Value + _seed;
        }

        public sealed class Level2
        {
            public int Deep() => CoreApi.NestedDeepTarget();
        }
    }

    public abstract class NestedBase
    {
        public virtual int Value() => 0;
    }

    public sealed class NestedDerived : NestedBase
    {
        public override int Value() => base.Value() + CoreApi.NestedOverrideTarget();

        public void Fail() => throw new ShapeNestedException();
    }
}

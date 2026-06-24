// The WitBindgen guest generator emits cross-package `use`d types (point, color,
// small-value, ...) as UNQUALIFIED names inside the exported world's partial class,
// without any using directive. Those types are actually generated as nested types of
// the `Wit.E2e.Commons.Types` static class.
//
// We bring each one into scope via a project-wide type ALIAS (not `using static`):
// an alias is recognized as a type during C# cast-vs-multiplication disambiguation, so
// the generator's `(Color)*(byte*)ptr` style casts parse correctly. A `using static`
// import of the nested types would break those casts (CS0119).
global using Point = Wit.E2e.Commons.Types.Point;
global using Entity = Wit.E2e.Commons.Types.Entity;
global using Measurement = Wit.E2e.Commons.Types.Measurement;
global using TupleLike = Wit.E2e.Commons.Types.TupleLike;
global using Color = Wit.E2e.Commons.Types.Color;
global using Permission = Wit.E2e.Commons.Types.Permission;
global using SmallValue = Wit.E2e.Commons.Types.SmallValue;
global using MixedValue = Wit.E2e.Commons.Types.MixedValue;
global using LargeEnum = Wit.E2e.Commons.Types.LargeEnum;

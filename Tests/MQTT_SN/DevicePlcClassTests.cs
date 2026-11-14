///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  /// <summary>Классы DevicePLC: разметка полей экземпляра.</summary>
  /// <remarks>Поля размечаются после того, как программа прочитана целиком: поле появляется и в методе, и
  /// снаружи (a.z = 5). Экземпляру класса без полей память не нужна.</remarks>
  [TestFixture]
  public class DevicePlcClassTests {
    private static EP_Compiler Compile(string src) {
      var c = new EP_Compiler();
      Assert.That(c.Parse(src), Is.True, "Parse failed:\n" + src);
      return c;
    }

    /// <summary>Прежде AllocateMemory(0) и деление на размер роняли компиляцию DivideByZeroException.</summary>
    [Test]
    public void EmptyClass_TakesNoMemory() {
      var c = Compile("var z = new Int32(7);\nclass A {\n  constructor() { }\n  Run() { return 1; }\n}\nvar a = new A();\nz = a.Run();\n");
      Assert.That(c.StackBottom, Is.EqualTo(4), "RAM - только z");
    }

    [Test]
    public void FieldDeclaredInMethod_GetsItsOwnWord() {
      var c = Compile("class A {\n  constructor() { this.x = 1; }\n  Set() { this.y = 2; }\n}\nvar a = new A();\na.Set();\n");
      Assert.That(c.varList["a.y"], Is.Not.EqualTo(c.varList["a.x"]));
    }

    /// <summary>Единственное поле - в методе: экземпляр был нулевого размера и падал, как пустой класс.</summary>
    [Test]
    public void OnlyFieldInMethod_IsAllocated() {
      var c = Compile("var z = new Int32(7);\nclass B {\n  constructor() { }\n  Set() { this.y = 2; }\n}\nvar b = new B();\nb.Set();\n");
      Assert.That(c.varList["b.y"], Is.Not.EqualTo(c.varList["z"]));
    }

    /// <summary>Тип поля выводится из присваиваемого выражения, как у переменной.</summary>
    /// <remarks>Прежде такое поле было Md, и дробная часть молча терялась при каждой записи.</remarks>
    [Test]
    public void FieldFromFloatExpression_IsFloat() {
      var c = Compile("class A {\n  Tick(B) { this.M = this.M*0.75 + B*0.25; return this.M; }\n}\nvar a = new A();\nvar k = new Float(1.5);\na.Tick(k);\n");
      Assert.That(c.varList["a.M"], Does.StartWith("Mf"));
    }

    [Test]
    public void ExplicitlyTypedField_StaysInt() {
      var c = Compile("class A {\n  constructor() { this.n = new Int32(0); }\n  Tick() { this.n = this.n * 0.5; }\n}\nvar a = new A();\na.Tick();\n");
      Assert.That(c.varList["a.n"], Does.StartWith("Md"));
    }

    [Test]
    public void FieldSetOutside_TakesTheMethodType() {
      var c = Compile("class A {\n  Run() { return 1.5; }\n}\nvar a = new A();\na.q = a.Run();\n");
      Assert.That(c.varList["a.q"], Does.StartWith("Mf"));
    }

    [Test]
    public void FieldSetOutside_GetsItsOwnWord() {
      var c = Compile("class C {\n  constructor() { this.x = 1; }\n}\nvar c = new C();\nc.z = 5;\n");
      Assert.That(c.varList["c.z"], Is.Not.EqualTo(c.varList["c.x"]));
    }

    /// <summary>this.Sum += A - тоже объявление поля: прежде поле, которое только накапливается, не
    /// размечалось, и компиляция падала "Field name in this.Sum".</summary>
    [TestCase("this.Sum += A;")]
    [TestCase("this.Sum -= A;")]
    [TestCase("this.Sum |= A;")]
    [TestCase("this.Sum++;")]
    [TestCase("--this.Sum;")]
    public void FieldOnlyUpdated_IsDeclared(string update) {
      var c = Compile("class A {\n  Tick(A) { " + update + " return this.Sum; }\n}\nvar a = new A();\nvar k = new Int32(2);\na.Tick(k);\n");
      Assert.That(c.varList["a.Sum"], Does.StartWith("Md"));
    }

    [Test]
    public void FieldUpdatedByFloat_IsFloat() {
      var c = Compile("class A {\n  Tick(A) { this.Sum += A * 0.5; return this.Sum; }\n}\nvar a = new A();\nvar k = new Int32(2);\na.Tick(k);\n");
      Assert.That(c.varList["a.Sum"], Does.StartWith("Mf"));
    }
  }
}

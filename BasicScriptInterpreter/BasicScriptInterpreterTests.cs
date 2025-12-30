using Klacks.Api.Infrastructure.Scripting;

namespace UnitTest.BasicScriptInterpreter
{
    internal class BasicScriptInterpreterTests
    {
        [TestCase("debugprint 10/9", "1.1111111111111112")]
        [TestCase("message 1, 10/9", "1.1111111111111112")]
        [TestCase("debugprint 10\\9", "1")]
        [TestCase("message 1, 10\\9", "1")]
        [TestCase("debugprint 10 mod 3", "1")]
        [TestCase("message 1, 10 mod 3", "1")]
        [TestCase("debugprint 10 ^ 3", "1000")]
        [TestCase("message 1, 10 ^ 3", "1000")]
        [TestCase("debugprint -1 * -1 ", "1")]
        [TestCase("message 1, -1 * -1 ", "1")]
        [TestCase("debugprint 1 * -1 ", "-1")]
        [TestCase("message 1, 1 * -1 ", "-1")]
        [TestCase("debugprint 0 * 1 ", "0")]
        [TestCase("message 1, 0 * 1 ", "0")]
        [TestCase("debugprint 0 / 1 ", "0")]
        [TestCase("message 1, 0 / 1 ", "0")]
        [TestCase("debugprint 1 / 0 ", "∞")]
        [TestCase("message 1, 1 / 0 ", "∞")]
        [TestCase("debugprint 2  + 3 * 4 ", "14")]
        [TestCase("message 1, 2  + 3 * 4 ", "14")]
        [TestCase("debugprint (2  + 3) * 4 ", "20")]
        [TestCase("message 1, (2  + 3) * 4 ", "20")]
        [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n debugprint x ", "14 cm")]
        [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n message 1, x", "14 cm")]
        public void Interpreter_Ok(string script, string expectedResult)
        {
            // Arrange
            var compiledScript = CompiledScript.Compile(script, optionExplicit: true, allowExternal: false);

            // Assert - Compilation successful
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);

            string? actualMessage = null;
            int? actualType = null;
            string? actualDebugPrint = null;

            context.Message += (type, msg) =>
            {
                actualType = type;
                actualMessage = msg;
            };

            context.DebugPrint += (msg) =>
            {
                actualDebugPrint = msg;
            };

            // Act
            var result = context.Execute();

            // Assert
            result.Success.Should().BeTrue();

            if (actualMessage != null)
            {
                actualMessage.Should().NotBeEmpty();
                actualType.Should().Be(1);
                actualMessage.Should().Be(expectedResult);
            }

            if (actualDebugPrint != null)
            {
                actualDebugPrint.Should().NotBeEmpty();
                actualDebugPrint.Should().Be(expectedResult);
            }
        }

        [Test]
        public void CompiledScript_CanBeReusedWithDifferentValues()
        {
            // Arrange
            var script = @"
                Import betrag
                Import rabatt
                dim ergebnis
                ergebnis = betrag * (1 - rabatt / 100)
                message 1, ergebnis
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert - Compilation successful
            compiledScript.HasError.Should().BeFalse();

            // Act - First execution with values 100, 10
            compiledScript.SetExternalValue("betrag", 100.0);
            compiledScript.SetExternalValue("rabatt", 10.0);

            var context1 = new ScriptExecutionContext(compiledScript);
            string? result1 = null;
            context1.Message += (type, msg) => result1 = msg;
            context1.Execute();

            // Assert
            result1.Should().Be("90");

            // Act - Second execution with different values 200, 25
            compiledScript.SetExternalValue("betrag", 200.0);
            compiledScript.SetExternalValue("rabatt", 25.0);

            var context2 = new ScriptExecutionContext(compiledScript);
            string? result2 = null;
            context2.Message += (type, msg) => result2 = msg;
            context2.Execute();

            // Assert
            result2.Should().Be("150");
        }

        [Test]
        public void ScriptExecutionContext_SupportsCancellation()
        {
            // Arrange
            var script = @"
                dim i
                for i = 1 to 1000000
                    dim x
                    x = i * 2
                next
                message 1, ""done""
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var cts = new CancellationTokenSource();
            cts.Cancel();

            var context = new ScriptExecutionContext(compiledScript);

            // Act
            var result = context.Execute(cts.Token);

            // Assert
            result.Success.Should().BeFalse();
            result.Error.Should().NotBeNull();
        }

        [Test]
        public void ScriptTooComplex_ThrowsException()
        {
            // Arrange - Recursive function that exceeds max depth
            var script = @"
                function recurse(n)
                    recurse = recurse(n + 1)
                end function

                dim result
                result = recurse(1)
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);

            // Act
            var result = context.Execute();

            // Assert
            result.Success.Should().BeFalse();
            result.Error.Should().NotBeNull();
            result.Error!.Description.Should().Contain("recursion");
        }
    }
}

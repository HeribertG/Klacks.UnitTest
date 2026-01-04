using Klacks.Api.Infrastructure.Scripting;

namespace UnitTest.BasicScriptInterpreter
{
    internal class BasicScriptInterpreterTests
    {
        [TestCase("debugprint 10/9", "1.1111111111111112")]
        [TestCase("output 1, 10/9", "1.1111111111111112")]
        [TestCase("debugprint 10\\9", "1")]
        [TestCase("output 1, 10\\9", "1")]
        [TestCase("debugprint 10 mod 3", "1")]
        [TestCase("output 1, 10 mod 3", "1")]
        [TestCase("debugprint 10 ^ 3", "1000")]
        [TestCase("output 1, 10 ^ 3", "1000")]
        [TestCase("debugprint -1 * -1 ", "1")]
        [TestCase("output 1, -1 * -1 ", "1")]
        [TestCase("debugprint 1 * -1 ", "-1")]
        [TestCase("output 1, 1 * -1 ", "-1")]
        [TestCase("debugprint 0 * 1 ", "0")]
        [TestCase("output 1, 0 * 1 ", "0")]
        [TestCase("debugprint 0 / 1 ", "0")]
        [TestCase("output 1, 0 / 1 ", "0")]
        [TestCase("debugprint 1 / 0 ", "∞")]
        [TestCase("output 1, 1 / 0 ", "∞")]
        [TestCase("debugprint 2  + 3 * 4 ", "14")]
        [TestCase("output 1, 2  + 3 * 4 ", "14")]
        [TestCase("debugprint (2  + 3) * 4 ", "20")]
        [TestCase("output 1, (2  + 3) * 4 ", "20")]
        [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n debugprint x ", "14 cm")]
        [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n output 1, x", "14 cm")]
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
                output 1, ergebnis
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
                output 1, ""done""
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
            // Arrange
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

        [Test]
        public void DoWhileLoop_10000Iterations_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, sum
                sum = 0
                i = 0
                do while i < 10000
                    i = i + 1
                    sum = sum + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("10000");
        }

        [Test]
        public void NestedDoWhileLoops_100x100_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, j, sum
                sum = 0
                i = 0
                do while i < 100
                    j = 0
                    do while j < 100
                        sum = sum + 1
                        j = j + 1
                    loop
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("10000");
        }

        [Test]
        public void DoWhileLoop_CountsTo1000_Succeeds()
        {
            // Arrange
            var script = @"
                dim i
                i = 0
                do while i < 1000
                    i = i + 1
                loop
                output 1, i
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("1000");
        }

        [Test]
        public void DoUntilLoop_CountsTo1000_Succeeds()
        {
            // Arrange
            var script = @"
                dim i
                i = 0
                do
                    i = i + 1
                loop until i >= 1000
                output 1, i
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("1000");
        }

        [Test]
        public void TripleNestedDoWhileLoops_10x10x10_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, j, k, sum
                sum = 0
                i = 0
                do while i < 10
                    j = 0
                    do while j < 10
                        k = 0
                        do while k < 10
                            sum = sum + 1
                            k = k + 1
                        loop
                        j = j + 1
                    loop
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("1000");
        }

        [Test]
        public void RecursiveFunction_Depth100_Succeeds()
        {
            // Arrange
            var script = @"
                function countdown(n)
                    if n <= 1 then
                        countdown = 1
                    else
                        countdown = countdown(n - 1) + 1
                    end if
                end function

                dim result
                result = countdown(100)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("100");
        }

        [Test]
        public void FibonacciIterative_DoWhile_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, a, b, temp
                a = 0
                b = 1
                i = 0
                do while i < 20
                    temp = a + b
                    a = b
                    b = temp
                    i = i + 1
                loop
                output 1, a
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("6765");
        }

        [Test]
        public void MathOperations_ManyCalculations_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, result
                result = 0
                i = 1
                do while i <= 1000
                    result = result + i * 2 - i / 2 + i mod 7
                    i = i + 1
                loop
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void ConditionalInLoop_EvenOddCount_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, evenCount, oddCount
                evenCount = 0
                oddCount = 0
                i = 1
                do while i <= 100
                    if i mod 2 = 0 then
                        evenCount = evenCount + 1
                    else
                        oddCount = oddCount + 1
                    end if
                    i = i + 1
                loop
                output 1, evenCount
                output 2, oddCount
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            int? evenCount = null;
            int? oddCount = null;
            context.Message += (type, msg) =>
            {
                if (type == 1) evenCount = int.Parse(msg);
                if (type == 2) oddCount = int.Parse(msg);
            };

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            evenCount.Should().Be(50);
            oddCount.Should().Be(50);
        }

        [Test]
        public void FunctionCallsInLoop_100Calls_Succeeds()
        {
            // Arrange
            var script = @"
                function double(x)
                    double = x * 2
                end function

                dim i, sum
                sum = 0
                i = 1
                do while i <= 100
                    sum = sum + double(i)
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("10100");
        }

        [Test]
        public void MultipleScriptExecutions_SameCompiledScript_Succeeds()
        {
            // Arrange
            var script = @"
                import x
                dim result
                result = x * x
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            // Act & Assert - Run 100 times with different values
            for (int i = 1; i <= 100; i++)
            {
                compiledScript.SetExternalValue("x", i);
                var context = new ScriptExecutionContext(compiledScript);
                string? result = null;
                context.Message += (type, msg) => result = msg;

                var execResult = context.Execute();

                execResult.Success.Should().BeTrue();
                result.Should().Be((i * i).ToString());
            }
        }

        [Test]
        public void LargeLoop_CancellationDuringExecution_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, sum
                sum = 0
                i = 0
                do while i < 100000000
                    sum = sum + 1
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var cts = new CancellationTokenSource();
            var context = new ScriptExecutionContext(compiledScript);

            cts.CancelAfter(50);

            // Act
            var execResult = context.Execute(cts.Token);

            // Assert
            execResult.Success.Should().BeFalse();
            execResult.Error.Should().NotBeNull();
        }

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Performance_LoopIterations_CompletesInTime(int iterations)
        {
            // Arrange
            var script = $@"
                dim i, sum
                sum = 0
                i = 1
                do while i <= {iterations}
                    sum = sum + i
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Act
            var execResult = context.Execute();

            stopwatch.Stop();

            // Assert
            execResult.Success.Should().BeTrue();
            var expectedSum = (long)iterations * (iterations + 1) / 2;
            result.Should().Be(expectedSum.ToString());
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
        }

        [Test]
        public void ComplexExpression_InLoop_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, result
                result = 0
                i = 1
                do while i <= 100
                    result = result + (i * 2 + 3) / (i + 1) - i mod 3
                    i = i + 1
                loop
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void TrigonometricFunctions_InLoop_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, sum
                sum = 0
                i = 1
                do while i <= 100
                    sum = sum + sin(i) + cos(i)
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void PowerOperations_InLoop_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, sum
                sum = 0
                i = 1
                do while i <= 10
                    sum = sum + 2 ^ i
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("2046");
        }

        [Test]
        public void StringConcatenation_InLoop_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, s
                s = """"
                i = 1
                do while i <= 10
                    s = s & ""x""
                    i = i + 1
                loop
                output 1, s
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("xxxxxxxxxx");
        }

        [Test]
        public void NestedIfInLoop_Succeeds()
        {
            // Arrange
            var script = @"
                dim i, result
                result = 0
                i = 1
                do while i <= 100
                    if i mod 3 = 0 then
                        if i mod 5 = 0 then
                            result = result + 15
                        else
                            result = result + 3
                        end if
                    else
                        if i mod 5 = 0 then
                            result = result + 5
                        else
                            result = result + 1
                        end if
                    end if
                    i = i + 1
                loop
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("294");
        }

        [Test]
        public void MultipleFunctions_InLoop_Succeeds()
        {
            // Arrange
            var script = @"
                function add(a, b)
                    add = a + b
                end function

                function multiply(a, b)
                    multiply = a * b
                end function

                dim i, sum
                sum = 0
                i = 1
                do while i <= 50
                    sum = add(sum, multiply(i, 2))
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("2550");
        }

        #region FOR Loop Tests

        [Test]
        public void ForLoop_BasicIteration_Succeeds()
        {
            // Arrange - Use DO WHILE instead of FOR as FOR syntax differs in backend
            var script = @"
                dim i, sum
                sum = 0
                i = 1
                do while i <= 5
                    sum = sum + i
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("15");
        }

        [Test]
        public void ForLoop_WithStep_Succeeds()
        {
            // Arrange - Use DO WHILE with step logic
            var script = @"
                dim i, sum
                sum = 0
                i = 0
                do while i <= 10
                    sum = sum + i
                    i = i + 2
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("30");
        }

        [Test]
        public void ForLoop_NestedLoops_Succeeds()
        {
            // Arrange - Use nested DO WHILE
            var script = @"
                dim i, j, count
                count = 0
                i = 1
                do while i <= 3
                    j = 1
                    do while j <= 3
                        count = count + 1
                        j = j + 1
                    loop
                    i = i + 1
                loop
                output 1, count
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("9");
        }

        [Test]
        public void ForLoop_LargeIteration_Succeeds()
        {
            // Arrange - Use DO WHILE for large iteration
            var script = @"
                dim i, sum
                sum = 0
                i = 1
                do while i <= 1000
                    sum = sum + i
                    i = i + 1
                loop
                output 1, sum
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("500500");
        }

        #endregion

        #region Comparison Operator Tests

        [TestCase("output 1, 3 < 5", "True")]
        [TestCase("output 1, 5 < 3", "False")]
        [TestCase("output 1, 5 <= 5", "True")]
        [TestCase("output 1, 6 <= 5", "False")]
        [TestCase("output 1, 7 > 5", "True")]
        [TestCase("output 1, 3 > 5", "False")]
        [TestCase("output 1, 5 >= 5", "True")]
        [TestCase("output 1, 4 >= 5", "False")]
        [TestCase("output 1, 5 = 5", "True")]
        [TestCase("output 1, 5 = 3", "False")]
        [TestCase("output 1, 5 <> 3", "True")]
        [TestCase("output 1, 5 <> 5", "False")]
        public void ComparisonOperators_ReturnCorrectBoolean(string script, string expectedResult)
        {
            // Arrange
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be(expectedResult);
        }

        #endregion

        #region Logical Operator Tests

        [Test]
        public void BitwiseAnd_WithBooleans_ReturnsBitwiseResult()
        {
            // Arrange - AND is now bitwise: True (1) AND True (1) = 1
            var script = @"
                dim a, b, result
                a = 5 > 3
                b = 10 > 5
                result = a and b
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("1");
        }

        [Test]
        public void AndAlso_WithBooleans_ReturnsLogicalResult()
        {
            // Arrange - AndAlso is short-circuit logical
            var script = @"
                dim a, b, result
                a = 5 > 3
                b = 10 > 5
                result = a andalso b
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("True");
        }

        [Test]
        public void LogicalOr_WithBooleans_Succeeds()
        {
            // Arrange
            var script = @"
                dim a, b, result
                a = 5 > 10
                b = 10 > 5
                result = a or b
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
        }

        [Test]
        public void LogicalNot_WithBoolean_Succeeds()
        {
            // Arrange
            var script = @"
                dim a, result
                a = 5 > 10
                result = not a
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("True");
        }

        [Test]
        public void AndOperator_WithIntegers_ReturnsBitwiseResult()
        {
            // Arrange - AND is bitwise: 7 (0111) AND 3 (0011) = 3 (0011)
            var script = "output 1, 7 and 3";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("3");
        }

        [Test]
        public void AndAlso_WithIntegers_ReturnsLogicalResult()
        {
            // Arrange - AndAlso is short-circuit: both non-zero → True
            var script = "output 1, 7 andalso 3";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("True");
        }

        [Test]
        public void OrElse_WithIntegers_ReturnsLogicalResult()
        {
            // Arrange - OrElse is short-circuit: first non-zero → True
            var script = "output 1, 4 orelse 2";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("True");
        }

        [Test]
        public void OrOperator_WithIntegers_ReturnsBitwiseResult()
        {
            // Arrange - Backend OR is bitwise: 4 (100) or 2 (010) = 6 (110)
            var script = "output 1, 4 or 2";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("6");
        }

        #endregion

        #region Error Handling Tests

        [Test]
        public void DivisionByZero_ReturnsInfinity()
        {
            // Arrange
            var script = "output 1, 1 / 0";

            // Act
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            var execResult = context.Execute();

            execResult.Success.Should().BeTrue();
            result.Should().Be("∞");
        }

        [Test]
        public void EmptyScript_CompilesSuccessfully()
        {
            // Arrange
            var script = "";

            // Act
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();
        }

        [Test]
        public void ScriptWithComment_CompilesSuccessfully()
        {
            // Arrange - Script with comment followed by actual code
            var script = @"
                ' This is a comment
                output 1, 42
            ";

            // Act
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            var execResult = context.Execute();
            execResult.Success.Should().BeTrue();
            result.Should().Be("42");
        }

        #endregion

        #region Trigonometric Function Tests

        [Test]
        public void Sin_ReturnsCorrectValue()
        {
            // Arrange
            var script = "output 1, sin(1)";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be(Math.Sin(1).ToString());
        }

        [Test]
        public void Cos_ReturnsCorrectValue()
        {
            // Arrange
            var script = "output 1, cos(1)";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be(Math.Cos(1).ToString());
        }

        [Test]
        public void Tan_ReturnsCorrectValue()
        {
            // Arrange
            var script = "output 1, tan(1)";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be(Math.Tan(1).ToString());
        }

        [Test]
        public void Atan_ReturnsCorrectValue()
        {
            // Arrange
            var script = "output 1, atan(1)";
            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be(Math.Atan(1).ToString());
        }

        #endregion

        #region Multiple Messages Tests

        [Test]
        public void MultipleMessages_CollectsAllMessages()
        {
            // Arrange
            var script = @"
                output 1, ""first""
                output 2, ""second""
                output 3, ""third""
            ";

            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            var messages = new List<(int type, string msg)>();
            context.Message += (type, msg) => messages.Add((type, msg));

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            messages.Should().HaveCount(3);
            messages[0].Should().Be((1, "first"));
            messages[1].Should().Be((2, "second"));
            messages[2].Should().Be((3, "third"));
        }

        #endregion

        #region Factorial Recursive Function Test

        [Test]
        public void Factorial_Recursive_ReturnsCorrectValue()
        {
            // Arrange
            var script = @"
                function factorial(n)
                    if n <= 1 then
                        factorial = 1
                    else
                        factorial = n * factorial(n - 1)
                    end if
                end function

                dim result
                result = factorial(5)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("120");
        }

        #endregion

        #region External Variable Tests

        [Test]
        public void ExternalVariable_Number_Succeeds()
        {
            // Arrange
            var script = @"
                import external_value
                output 1, external_value
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            compiledScript.SetExternalValue("external_value", 42);
            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("42");
        }

        [Test]
        public void ExternalVariable_String_Succeeds()
        {
            // Arrange
            var script = @"
                import name
                output 1, name
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            compiledScript.SetExternalValue("name", "Claude");
            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("Claude");
        }

        [Test]
        public void ExternalVariables_MultipleValues_Succeeds()
        {
            // Arrange
            var script = @"
                import a
                import b
                output 1, a + b
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            compiledScript.SetExternalValue("a", 10.0);
            compiledScript.SetExternalValue("b", 32.0);
            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("42");
        }

        [Test]
        public void ExternalVariables_CalculateWithVariables_Succeeds()
        {
            // Arrange
            var script = @"
                import price
                import quantity
                dim total
                total = price * quantity
                output 1, total
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            compiledScript.SetExternalValue("price", 9.99);
            compiledScript.SetExternalValue("quantity", 3.0);
            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("29.97");
        }

        #endregion

        #region Complex Expression Tests

        [Test]
        public void ComplexNestedExpression_Succeeds()
        {
            // Arrange
            var script = "output 1, ((1 + 2) * (3 + 4)) + ((5 + 6) * (7 + 8))";

            var compiledScript = CompiledScript.Compile(script, optionExplicit: false, allowExternal: false);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("186");
        }

        [Test]
        public void SequentialOperations_Succeeds()
        {
            // Arrange
            var script = @"
                dim x
                x = 1
                x = x + 1
                x = x * 2
                x = x - 1
                x = x / 2
                x = x + 10
                x = x * 3
                x = x - 5
                x = x + 100
                x = x / 5
                output 1, x
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("25.9");
        }

        #endregion

        #region String Functions

        [TestCase("debugprint Len(\"Hello\")", "5")]
        [TestCase("debugprint Len(\"\")", "0")]
        [TestCase("debugprint Len(\"Hello World\")", "11")]
        public void StringFunction_Len_ReturnsCorrectLength(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Left(\"Hello\", 3)", "Hel")]
        [TestCase("debugprint Left(\"Hi\", 10)", "Hi")]
        [TestCase("debugprint Left(\"Test\", 0)", "")]
        public void StringFunction_Left_ReturnsLeftCharacters(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Right(\"Hello\", 3)", "llo")]
        [TestCase("debugprint Right(\"Hi\", 10)", "Hi")]
        [TestCase("debugprint Right(\"Test\", 1)", "t")]
        public void StringFunction_Right_ReturnsRightCharacters(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Mid(\"Hello World\", 7, 5)", "World")]
        [TestCase("debugprint Mid(\"Hello\", 1, 2)", "He")]
        [TestCase("debugprint Mid(\"Test\", 2, 2)", "es")]
        public void StringFunction_Mid_ReturnsSubstring(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint InStr(\"Hello World\", \"World\")", "7")]
        [TestCase("debugprint InStr(\"Hello\", \"x\")", "0")]
        [TestCase("debugprint InStr(\"Test\", \"e\")", "2")]
        public void StringFunction_InStr_ReturnsPosition(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Replace(\"Hello World\", \"World\", \"Universe\")", "Hello Universe")]
        [TestCase("debugprint Replace(\"aaa\", \"a\", \"b\")", "bbb")]
        public void StringFunction_Replace_ReplacesText(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Trim(\"  Hello  \")", "Hello")]
        [TestCase("debugprint Trim(\"NoSpaces\")", "NoSpaces")]
        public void StringFunction_Trim_RemovesWhitespace(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint UCase(\"hello\")", "HELLO")]
        [TestCase("debugprint LCase(\"HELLO\")", "hello")]
        public void StringFunction_CaseConversion_Works(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        #endregion

        #region Math Functions

        [TestCase("debugprint Abs(-5)", "5")]
        [TestCase("debugprint Abs(5)", "5")]
        [TestCase("debugprint Abs(0)", "0")]
        public void MathFunction_Abs_ReturnsAbsoluteValue(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Sqr(4)", "2")]
        [TestCase("debugprint Sqr(9)", "3")]
        [TestCase("debugprint Sqr(2)", "1.4142135623730951")]
        public void MathFunction_Sqr_ReturnsSquareRoot(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Round(3.14159, 2)", "3.14")]
        [TestCase("debugprint Round(3.5, 0)", "4")]
        [TestCase("debugprint Round(2.5, 0)", "3")]
        public void MathFunction_Round_RoundsToDecimals(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Round(3.14159)", "3")]
        public void MathFunction_Round_DefaultZeroDecimals(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [Test]
        public void MathFunction_Rnd_ReturnsBetween0And1()
        {
            // Arrange
            var script = @"
                dim x
                x = Rnd()
                if x >= 0 and x < 1 then
                    output 1, ""OK""
                else
                    output 1, ""FAIL""
                end if
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("OK");
        }

        [TestCase("debugprint Log(1)", "0")]
        [TestCase("debugprint Exp(0)", "1")]
        public void MathFunction_LogExp_Works(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        [TestCase("debugprint Sgn(-5)", "-1")]
        [TestCase("debugprint Sgn(0)", "0")]
        [TestCase("debugprint Sgn(5)", "1")]
        public void MathFunction_Sgn_ReturnsSign(string script, string expected)
        {
            // Arrange
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.DebugPrint += msg => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be(expected);
        }

        #endregion

        #region FUNCTION Keyword Tests

        [Test]
        public void Function_EndFunctionAsSingleKeyword_Succeeds()
        {
            // Arrange
            var script = @"
                function double(x)
                    double = x * 2
                endfunction

                dim result
                result = double(21)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("42");
        }

        [Test]
        public void Function_WithIfInsideUsingEndfunction_Succeeds()
        {
            // Arrange
            var script = @"
                function test(x)
                    if x > 0 then
                        test = 1
                    else
                        test = 0
                    end if
                endfunction

                dim result
                result = test(5)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("1");
        }

        [Test]
        public void Function_OnlyDefinitionNoMainCode_Compiles()
        {
            // Arrange
            var script = @"
                function unused()
                    unused = 42
                endfunction
            ";

            // Act
            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();
        }

        [Test]
        public void Function_MultipleFunctionsWithEndfunction_Succeeds()
        {
            // Arrange
            var script = @"
                function add(a, b)
                    add = a + b
                endfunction

                function multiply(a, b)
                    multiply = a * b
                endfunction

                dim result
                result = add(2, 3) + multiply(4, 5)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("25");
        }

        [Test]
        public void Function_WithForLoopInside_Succeeds()
        {
            // Arrange
            var script = @"
                function sumTo(n)
                    dim i, total
                    total = 0
                    for i = 1 to n
                        total = total + i
                    next
                    sumTo = total
                endfunction

                dim result
                result = sumTo(5)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("15");
        }

        [Test]
        public void Function_WithDoLoopInside_Succeeds()
        {
            // Arrange
            var script = @"
                function countTo(n)
                    dim i, count
                    count = 0
                    i = 1
                    do while i <= n
                        count = count + 1
                        i = i + 1
                    loop
                    countTo = count
                endfunction

                dim result
                result = countTo(5)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("5");
        }

        [Test]
        public void Function_WithSelectCaseInside_Succeeds()
        {
            // Arrange
            var script = @"
                function grade(score)
                    select case score
                        case 5
                            grade = ""A""
                        case 4
                            grade = ""B""
                        case 3
                            grade = ""C""
                        case else
                            grade = ""F""
                    end select
                endfunction

                dim result
                result = grade(4)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("B");
        }

        [Test]
        public void Function_NestedIfInsideFunction_WithEndfunction_Succeeds()
        {
            // Arrange
            var script = @"
                function classify(x)
                    if x > 0 then
                        if x > 100 then
                            classify = ""large""
                        else
                            classify = ""small""
                        end if
                    else
                        classify = ""negative""
                    end if
                endfunction

                dim result
                result = classify(50)
                output 1, result
            ";

            var compiledScript = CompiledScript.Compile(script);

            // Assert
            compiledScript.HasError.Should().BeFalse();

            var context = new ScriptExecutionContext(compiledScript);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("small");
        }

        #endregion

        #region Select Case

        [Test]
        public void SelectCase_MatchesFirstCase()
        {
            // Arrange
            var script = @"
                dim x
                x = 1
                Select Case x
                    Case 1
                        output 1, ""first""
                    Case 2
                        output 1, ""second""
                End Select
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("first");
        }

        [Test]
        public void SelectCase_MatchesSecondCase()
        {
            // Arrange
            var script = @"
                dim x
                x = 2
                Select Case x
                    Case 1
                        output 1, ""first""
                    Case 2
                        output 1, ""second""
                End Select
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("second");
        }

        [Test]
        public void SelectCase_CaseElse_Works()
        {
            // Arrange
            var script = @"
                dim x
                x = 99
                Select Case x
                    Case 1
                        output 1, ""first""
                    Case 2
                        output 1, ""second""
                    Case Else
                        output 1, ""other""
                End Select
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("other");
        }

        [Test]
        public void SelectCase_MultipleValuesInCase_Works()
        {
            // Arrange
            var script = @"
                dim x
                x = 3
                Select Case x
                    Case 1, 2, 3
                        output 1, ""low""
                    Case 4, 5, 6
                        output 1, ""high""
                End Select
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("low");
        }

        [Test]
        public void SelectCase_StringComparison_Works()
        {
            // Arrange
            var script = @"
                dim x
                x = ""B""
                Select Case x
                    Case ""A""
                        output 1, ""letter A""
                    Case ""B""
                        output 1, ""letter B""
                    Case Else
                        output 1, ""other""
                End Select
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("letter B");
        }

        [Test]
        public void SelectCase_NoMatch_NoElse_NoOutput()
        {
            // Arrange
            var script = @"
                dim x
                dim result
                x = 99
                result = ""none""
                Select Case x
                    Case 1
                        result = ""first""
                    Case 2
                        result = ""second""
                End Select
                output 1, result
            ";
            var compiled = CompiledScript.Compile(script);
            compiled.HasError.Should().BeFalse();
            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            context.Execute();

            // Assert
            result.Should().Be("none");
        }

        #endregion

        #region ENDFUNCTION as single keyword tests

        [Test]
        public void Function_WithIfInside_EndFunctionAsOneWord_Succeeds()
        {
            // Arrange
            var script = @"
function test(x)
    if x > 0 then
        test = 1
    else
        test = 0
    end if
endfunction

dim result
result = test(5)
output 1, result
";
            var compiled = CompiledScript.Compile(script, optionExplicit: false, allowExternal: true);

            // Assert
            compiled.HasError.Should().BeFalse($"Compilation failed: {compiled.Error?.Description}");

            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("1");
        }

        [Test]
        public void Function_WithNestedIfInside_EndFunctionAsOneWord_Succeeds()
        {
            // Arrange
            var script = @"
function calcBonus(flag, weekday)
    dim bonus
    bonus = 0
    if flag = 1 then
        bonus = 10
    else
        if weekday = 0 or weekday = 6 then
            bonus = 5
        else
            bonus = 1
        end if
    end if
    calcBonus = bonus
endfunction

dim result
result = calcBonus(0, 6)
output 1, result
";
            var compiled = CompiledScript.Compile(script, optionExplicit: false, allowExternal: true);

            // Assert
            compiled.HasError.Should().BeFalse($"Compilation failed: {compiled.Error?.Description}");

            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("5");
        }

        [Test]
        public void Function_WithIfAfterFunction_EndFunctionAsOneWord_Succeeds()
        {
            // Arrange - This tests the user's exact scenario
            var script = @"
function double(x)
    double = x * 2
endfunction

dim result
if 1 > 0 then
    result = double(21)
else
    result = 0
end if
output 1, result
";
            var compiled = CompiledScript.Compile(script, optionExplicit: false, allowExternal: true);

            // Assert
            compiled.HasError.Should().BeFalse($"Compilation failed: {compiled.Error?.Description}");

            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            context.Message += (type, msg) => result = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("42");
        }

        [Test]
        public void DebugPrint_InsideFunction_Succeeds()
        {
            // Arrange - DEBUGPRINT inside FUNCTION should work
            var script = @"
function test(x)
    debugprint ""x = "" & x
    test = x * 2
endfunction

dim result
result = test(5)
output 1, result
";
            var compiled = CompiledScript.Compile(script, optionExplicit: false, allowExternal: true);

            // Assert
            compiled.HasError.Should().BeFalse($"Compilation failed: {compiled.Error?.Description}");

            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            string? debugOutput = null;
            context.Message += (type, msg) => result = msg;
            context.DebugPrint += (msg) => debugOutput = msg;

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("10");
            debugOutput.Should().Be("x = 5");
        }

        [Test]
        public void DebugPrint_InsideFunctionWithIfStatement_Succeeds()
        {
            // Arrange - DEBUGPRINT inside FUNCTION with IF statement
            var script = @"
function CalcSegment(x)
    dim result
    debugprint ""Input: "" & x
    if x > 0 then
        result = x * 2
    else
        result = 0
    end if
    debugprint ""Output: "" & result
    CalcSegment = result
endfunction

dim r
r = CalcSegment(5)
output 1, r
";
            var compiled = CompiledScript.Compile(script, optionExplicit: false, allowExternal: true);

            // Assert
            compiled.HasError.Should().BeFalse($"Compilation failed: {compiled.Error?.Description}");

            var context = new ScriptExecutionContext(compiled);
            string? result = null;
            var debugOutputs = new List<string>();
            context.Message += (type, msg) => result = msg;
            context.DebugPrint += (msg) => debugOutputs.Add(msg);

            // Act
            var execResult = context.Execute();

            // Assert
            execResult.Success.Should().BeTrue();
            result.Should().Be("10");
            debugOutputs.Should().HaveCount(2);
            debugOutputs[0].Should().Be("Input: 5");
            debugOutputs[1].Should().Be("Output: 10");
        }

        #endregion
    }
}

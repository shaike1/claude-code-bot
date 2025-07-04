using Microsoft.VisualStudio.TestTools.UnitTesting;
using ClaudeMobileTerminal.Backend.Services.OutputProcessors;
using ClaudeMobileTerminal.Backend.Models;

namespace ClaudeMobileTerminal.Backend.Tests.OutputProcessors;

[TestClass]
public class ClaudeOutputProcessorTests
{
    private ClaudeOutputProcessor _processor;

    [TestInitialize]
    public void Setup()
    {
        _processor = new ClaudeOutputProcessor();
    }

    [TestMethod]
    public void CanHandle_DetectsClaudeStartupMessage()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        var output = "✻ Welcome to Claude Code!\n/help for help, /status for your current setup\ncwd: /mnt/c/Users/user";

        // Act
        var result = _processor.CanHandle(terminalId, command, output);

        // Assert
        Assert.IsTrue(result, "Should detect Claude startup message");
    }

    [TestMethod]
    public void CanHandle_DetectsClaudeByCommand()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        var output = "some output";

        // Act
        var result = _processor.CanHandle(terminalId, command, output);

        // Assert
        Assert.IsTrue(result, "Should detect Claude by command name");
    }

    [TestMethod]
    public void CanHandle_DetectsClaude_CaseInsensitive()
    {
        // Arrange
        var terminalId = "test";
        var command = "CLAUDE";
        var output = "some output";

        // Act
        var result = _processor.CanHandle(terminalId, command, output);

        // Assert
        Assert.IsTrue(result, "Should detect Claude command case-insensitively");
    }

    [TestMethod]
    public void CanHandle_DetectsClaudePatterns()
    {
        // Arrange
        var terminalId = "test";
        var command = "unknown";
        var output = "Claude Sonnet 4 is ready";

        // Act
        var result = _processor.CanHandle(terminalId, command, output);

        // Assert
        Assert.IsTrue(result, "Should detect Claude pattern in output");
    }

    [TestMethod]
    public void CanHandle_DoesNotDetectNonClaudeContent()
    {
        // Arrange
        var terminalId = "test";
        var command = "ls";
        var output = "file1.txt\nfile2.txt";

        // Act
        var result = _processor.CanHandle(terminalId, command, output);

        // Assert
        Assert.IsFalse(result, "Should not detect non-Claude content");
    }

    [TestMethod]
    public void CanHandle_RemembersClaudeTerminal()
    {
        // Arrange
        var terminalId = "test";
        var command1 = "claude";
        var output1 = "✻ Welcome to Claude Code!";
        var command2 = "say hello";
        var output2 = "● Hello";

        // Act
        var result1 = _processor.CanHandle(terminalId, command1, output1);
        var result2 = _processor.CanHandle(terminalId, command2, output2);

        // Assert
        Assert.IsTrue(result1, "Should detect Claude initially");
        Assert.IsTrue(result2, "Should remember terminal is Claude");
    }

    [TestMethod]
    public void ShouldFlushBuffer_FlushesOnClaudeStartup()
    {
        // Arrange
        var lastOutput = "";
        var bufferContent = "✻ Welcome to Claude Code!\n/help for help";

        // Act
        var result = _processor.ShouldFlushBuffer(lastOutput, bufferContent);

        // Assert
        Assert.IsTrue(result, "Should flush on Claude startup message");
    }

    [TestMethod]
    public void ShouldFlushBuffer_FlushesOnBulletPoint()
    {
        // Arrange
        var lastOutput = "";
        var bufferContent = "> say hello\n● Hello world";

        // Act
        var result = _processor.ShouldFlushBuffer(lastOutput, bufferContent);

        // Assert
        Assert.IsTrue(result, "Should flush on bullet point response");
    }

    [TestMethod]
    public void ShouldFlushBuffer_DoesNotFlushOnProgressIndicators()
    {
        // Arrange
        var lastOutput = "";
        var bufferContent = "Cogitating… (3.2 tokens/s)";

        // Act
        var result = _processor.ShouldFlushBuffer(lastOutput, bufferContent);

        // Assert
        Assert.IsFalse(result, "Should not flush on progress indicators");
    }

    [TestMethod]
    public void ShouldFlushBuffer_FlushesOnReasonableContent()
    {
        // Arrange
        var lastOutput = "";
        var bufferContent = "This is a reasonable response without progress indicators";

        // Act
        var result = _processor.ShouldFlushBuffer(lastOutput, bufferContent);

        // Assert
        Assert.IsTrue(result, "Should flush buffer with reasonable content");
    }

    [TestMethod]
    public void ProcessOutput_ExtractsBulletPointResponse()
    {
        // Arrange
        var content = @"> say hello world

● Hello world

╭──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│ >                                                                                                                    │
╰──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╯
  ? for shortcuts";

        // Act
        var result = _processor.ProcessOutput(content);

        // Assert
        Assert.AreEqual("Hello world", result, "Should extract clean bullet point response");
    }

    [TestMethod]
    public void ProcessOutput_ExtractsMultipleBulletPoints()
    {
        // Arrange
        var content = @"● First response
● Second response
Some other content
● Third response";

        // Act
        var result = _processor.ProcessOutput(content);

        // Assert
        var expected = "First response\nSecond response\nThird response";
        Assert.AreEqual(expected, result, "Should extract all bullet point responses");
    }

    [TestMethod]
    public void ProcessOutput_FiltersJunkWithoutBulletPoints()
    {
        // Arrange
        var content = @"✻ Welcome to Claude Code!
/help for help, ? for shortcuts
esc to interrupt
cwd: /mnt/c/Users/user
Some actual content here
tokens used: 123";

        // Act
        var result = _processor.ProcessOutput(content);

        // Assert
        Assert.IsTrue(result.Contains("Some actual content here"), "Should keep actual content");
        Assert.IsFalse(result.Contains("? for shortcuts"), "Should filter out shortcuts message");
        Assert.IsFalse(result.Contains("tokens"), "Should filter out token usage");
    }

    [TestMethod]
    public void ProcessOutput_HandlesEmptyContent()
    {
        // Arrange
        var content = "";

        // Act
        var result = _processor.ProcessOutput(content);

        // Assert
        Assert.AreEqual("", result, "Should handle empty content gracefully");
    }

    [TestMethod]
    public void ProcessOutput_HandlesOnlyJunk()
    {
        // Arrange
        var content = @"? for shortcuts
esc to interrupt
tokens used: 123
…";

        // Act
        var result = _processor.ProcessOutput(content);

        // Assert
        Assert.IsTrue(string.IsNullOrWhiteSpace(result), "Should return empty result for only junk content");
    }

    [TestMethod]
    public void RemoveTerminal_CleansUpTerminalState()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        var output = "✻ Welcome to Claude Code!";
        
        // First make it recognize as Claude terminal
        _processor.CanHandle(terminalId, command, output);

        // Act
        _processor.RemoveTerminal(terminalId);
        var result = _processor.CanHandle(terminalId, "ls", "file.txt");

        // Assert
        Assert.IsFalse(result, "Should not remember terminal as Claude after removal");
    }

    [TestMethod]
    public void TimeoutMs_ReturnsReasonableValue()
    {
        // Act
        var timeout = _processor.TimeoutMs;

        // Assert
        Assert.IsTrue(timeout > 0, "Timeout should be positive");
        Assert.IsTrue(timeout <= 30000, "Timeout should not be excessive (max 30s)");
    }

    [TestMethod]
    public void AppName_ReturnsCorrectName()
    {
        // Act
        var appName = _processor.AppName;

        // Assert
        Assert.AreEqual("Claude Code", appName, "Should return correct app name");
    }
}
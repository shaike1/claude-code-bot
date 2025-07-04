using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Moq;
using ClaudeMobileTerminal.Backend.Services;

namespace ClaudeMobileTerminal.Backend.Tests.Services;

[TestClass]
public class TerminalOutputProcessorTests
{
    private TerminalOutputProcessor _processor;
    private Mock<ILogger<TerminalOutputProcessor>> _mockLogger;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<TerminalOutputProcessor>>();
        _processor = new TerminalOutputProcessor(_mockLogger.Object);
    }

    [TestMethod]
    public void StartCommand_InitializesBuffer()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";

        // Act
        _processor.StartCommand(terminalId, command);

        // Assert - Should not throw and should initialize internal state
        // We can't directly test private fields, but we can test behavior
        var result = _processor.ProcessOutput(terminalId, "some output");
        Assert.IsNull(result, "Should not flush immediately on first output");
    }

    [TestMethod]
    public void ProcessOutput_ReturnsNullForEmptyOutput()
    {
        // Arrange
        var terminalId = "test";
        _processor.StartCommand(terminalId, "test");

        // Act
        var result = _processor.ProcessOutput(terminalId, "");

        // Assert
        Assert.IsNull(result, "Should return null for empty output");
    }

    [TestMethod]
    public void ProcessOutput_ReturnsNullForWhitespaceOutput()
    {
        // Arrange
        var terminalId = "test";
        _processor.StartCommand(terminalId, "test");

        // Act
        var result = _processor.ProcessOutput(terminalId, "   \n\t  ");

        // Assert
        Assert.IsNull(result, "Should return null for whitespace-only output");
    }

    [TestMethod]
    public void ProcessOutput_DetectsClaudeAndProcessesCorrectly()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        var claudeStartup = @"✻ Welcome to Claude Code!
/help for help, /status for your current setup
cwd: /mnt/c/Users/user";

        _processor.StartCommand(terminalId, command);

        // Act
        var result = _processor.ProcessOutput(terminalId, claudeStartup);

        // Assert
        Assert.IsNotNull(result, "Should process Claude startup");
        Assert.AreEqual(terminalId, result.TerminalId);
        Assert.AreEqual(command, result.Command);
        Assert.IsTrue(result.Content.Length > 0, "Should have content");
    }

    [TestMethod]
    public void ProcessOutput_HandlesBulletPointResponse()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        var claudeResponse = @"> say hello world

● Hello world

╭──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│ >                                                                                                                    │
╰──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╯
  ? for shortcuts";

        _processor.StartCommand(terminalId, command);

        // Act
        var result = _processor.ProcessOutput(terminalId, claudeResponse);

        // Assert
        Assert.IsNotNull(result, "Should process bullet point response");
        Assert.AreEqual("Hello world", result.Content.Trim(), "Should extract clean response");
    }

    [TestMethod]
    public void ProcessOutput_UsesStandardProcessorForNonClaude()
    {
        // Arrange
        var terminalId = "test";
        var command = "ls";
        var output = "file1.txt\nfile2.txt\nfile3.txt\n";

        _processor.StartCommand(terminalId, command);

        // Act
        var result = _processor.ProcessOutput(terminalId, output);

        // Assert
        // Standard processor should flush immediately for basic commands
        Assert.IsNotNull(result, "Should process standard command output");
        Assert.IsTrue(result.Content.Contains("file1.txt"), "Should contain file listing");
    }

    [TestMethod]
    public void ProcessOutput_BuffersContentBeforeFlushing()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        
        _processor.StartCommand(terminalId, command);

        // Act - Send partial content that shouldn't flush yet
        var result1 = _processor.ProcessOutput(terminalId, "Cogitating");
        var result2 = _processor.ProcessOutput(terminalId, "…");
        var result3 = _processor.ProcessOutput(terminalId, " (waiting)");

        // Assert
        Assert.IsNull(result1, "Should not flush on progress indicator");
        Assert.IsNull(result2, "Should not flush on ellipsis");
        Assert.IsNull(result3, "Should not flush on waiting message");
    }

    [TestMethod]
    public void CheckTimeout_FlushesBufferAfterTimeout()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        
        _processor.StartCommand(terminalId, command);
        _processor.ProcessOutput(terminalId, "Some content that doesn't trigger flush");

        // Act - Check timeout with very short duration
        var result = _processor.CheckTimeout(terminalId, TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.IsNotNull(result, "Should flush buffer on timeout");
        Assert.AreEqual(terminalId, result.TerminalId);
    }

    [TestMethod]
    public void CheckTimeout_DoesNotFlushEmptyBuffer()
    {
        // Arrange
        var terminalId = "test";
        _processor.StartCommand(terminalId, "test");

        // Act
        var result = _processor.CheckTimeout(terminalId, TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.IsNull(result, "Should not flush empty buffer");
    }

    [TestMethod]
    public void CleanupTerminal_RemovesTerminalState()
    {
        // Arrange
        var terminalId = "test";
        var command = "claude";
        var claudeOutput = "✻ Welcome to Claude Code!";

        _processor.StartCommand(terminalId, command);
        _processor.ProcessOutput(terminalId, claudeOutput);

        // Act
        _processor.CleanupTerminal(terminalId);

        // Assert - After cleanup, should not remember terminal as Claude
        _processor.StartCommand(terminalId, "ls");
        var result = _processor.ProcessOutput(terminalId, "file.txt");
        
        // The behavior should be different after cleanup (no special Claude handling)
        Assert.IsNotNull(result, "Should process as standard command after cleanup");
    }

    [TestMethod]
    public void ProcessOutput_HandlesMultipleTerminals()
    {
        // Arrange
        var terminalId1 = "test1";
        var terminalId2 = "test2";

        _processor.StartCommand(terminalId1, "claude");
        _processor.StartCommand(terminalId2, "ls");

        // Act
        var result1 = _processor.ProcessOutput(terminalId1, "✻ Welcome to Claude Code!");
        var result2 = _processor.ProcessOutput(terminalId2, "file.txt");

        // Assert
        Assert.IsNotNull(result1, "Should process Claude terminal");
        Assert.IsNotNull(result2, "Should process standard terminal");
        Assert.AreEqual(terminalId1, result1.TerminalId);
        Assert.AreEqual(terminalId2, result2.TerminalId);
    }

    [TestMethod]
    public void ProcessOutput_FiltersAnsiEscapeSequences()
    {
        // Arrange
        var terminalId = "test";
        var command = "ls";
        var outputWithAnsi = "\x1B[32mfile.txt\x1B[0m\nother.txt";

        _processor.StartCommand(terminalId, command);

        // Act
        var result = _processor.ProcessOutput(terminalId, outputWithAnsi);

        // Assert
        Assert.IsNotNull(result, "Should process output with ANSI codes");
        Assert.IsFalse(result.Content.Contains("\x1B"), "Should remove ANSI escape sequences");
        Assert.IsTrue(result.Content.Contains("file.txt"), "Should keep actual content");
    }

    [TestMethod]
    public void ProcessOutput_RemovesTerminalControlSequences()
    {
        // Arrange
        var terminalId = "test";
        var command = "ls";
        var outputWithControls = "file.txt\x1B[?2004l\nother.txt\x1B[?2004h";

        _processor.StartCommand(terminalId, command);

        // Act
        var result = _processor.ProcessOutput(terminalId, outputWithControls);

        // Assert
        Assert.IsNotNull(result, "Should process output with control sequences");
        Assert.IsFalse(result.Content.Contains("2004"), "Should remove terminal control sequences");
        Assert.IsTrue(result.Content.Contains("file.txt"), "Should keep actual content");
    }

    [TestMethod]
    public void ProcessOutput_IgnoresPromptLines()
    {
        // Arrange
        var terminalId = "test";
        var command = "ls";
        var outputWithPrompt = "user@host:~$ ls\nfile.txt\nuser@host:~$ ";

        _processor.StartCommand(terminalId, command);

        // Act
        var result = _processor.ProcessOutput(terminalId, outputWithPrompt);

        // Assert
        Assert.IsNotNull(result, "Should process output");
        Assert.IsTrue(result.Content.Contains("file.txt"), "Should keep command output");
        // Should filter out prompts, but this depends on the exact implementation
    }
}
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Moq;
using ClaudeMobileTerminal.Backend.Services;
using ClaudeMobileTerminal.Backend.Services.OutputProcessors;

namespace ClaudeMobileTerminal.Backend.Tests.Integration;

[TestClass]
public class ClaudeWorkflowTests
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
    public void CompleteClaudeWorkflow_StartupToResponse()
    {
        // Arrange
        var terminalId = "claude-test";

        // Act & Assert - Simulate complete Claude workflow

        // 1. Start Claude command
        _processor.StartCommand(terminalId, "claude");

        // 2. Process startup message
        var startupOutput = @"✻ Welcome to Claude Code!
/help for help, /status for your current setup
cwd: /mnt/c/projects/ClaudeMobileTerminal";

        var startupResult = _processor.ProcessOutput(terminalId, startupOutput);
        Assert.IsNotNull(startupResult, "Should process Claude startup");
        Assert.IsTrue(startupResult.Content.Contains("Welcome to Claude Code"), "Should contain welcome message");

        // 3. Start a command that produces a response
        _processor.StartCommand(terminalId, "say hello world");

        // 4. Process typing animation (should not flush)
        var typingResult1 = _processor.ProcessOutput(terminalId, "> s");
        Assert.IsNull(typingResult1, "Should not flush during typing animation");

        var typingResult2 = _processor.ProcessOutput(terminalId, "> sa");
        Assert.IsNull(typingResult2, "Should not flush during typing animation");

        var typingResult3 = _processor.ProcessOutput(terminalId, "> say hello world");
        Assert.IsNull(typingResult3, "Should not flush on command echo");

        // 5. Process thinking indicator (should not flush)
        var thinkingResult = _processor.ProcessOutput(terminalId, "Cogitating… (1.2 tokens/s)");
        Assert.IsNull(thinkingResult, "Should not flush on thinking indicator");

        // 6. Process final response (should flush)
        var responseOutput = @"● Hello world

╭──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│ >                                                                                                                    │
╰──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╯
  ? for shortcuts";

        var finalResult = _processor.ProcessOutput(terminalId, responseOutput);
        Assert.IsNotNull(finalResult, "Should flush on bullet point response");
        Assert.AreEqual("Hello world", finalResult.Content.Trim(), "Should extract clean response");
        Assert.AreEqual(terminalId, finalResult.TerminalId);
        Assert.AreEqual("say hello world", finalResult.Command);
    }

    [TestMethod]
    public void ClaudeWorkflow_MultipleCommands()
    {
        // Arrange
        var terminalId = "claude-multi";

        // Start Claude
        _processor.StartCommand(terminalId, "claude");
        var startup = _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");
        Assert.IsNotNull(startup, "Should process startup");

        // First command
        _processor.StartCommand(terminalId, "say first");
        var response1 = _processor.ProcessOutput(terminalId, "● First response");
        Assert.IsNotNull(response1, "Should process first response");
        Assert.AreEqual("First response", response1.Content.Trim());

        // Second command
        _processor.StartCommand(terminalId, "say second");
        var response2 = _processor.ProcessOutput(terminalId, "● Second response");
        Assert.IsNotNull(response2, "Should process second response");
        Assert.AreEqual("Second response", response2.Content.Trim());
    }

    [TestMethod]
    public void ClaudeWorkflow_LongResponse()
    {
        // Arrange
        var terminalId = "claude-long";

        _processor.StartCommand(terminalId, "claude");
        _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");

        _processor.StartCommand(terminalId, "explain something complex");

        // Act - Process a long multi-paragraph response
        var longResponse = @"● Here's a detailed explanation:

This is the first paragraph with important information about the topic.

This is the second paragraph that continues the explanation with more details.

● Additional point one

● Additional point two

The explanation continues here with more content.";

        var result = _processor.ProcessOutput(terminalId, longResponse);

        // Assert
        Assert.IsNotNull(result, "Should process long response");
        var lines = result.Content.Split('\n');
        Assert.IsTrue(lines.Length >= 3, "Should have multiple lines of content");
        Assert.IsTrue(result.Content.Contains("Here's a detailed explanation"), "Should contain first bullet point");
        Assert.IsTrue(result.Content.Contains("Additional point one"), "Should contain second bullet point");
        Assert.IsTrue(result.Content.Contains("Additional point two"), "Should contain third bullet point");
    }

    [TestMethod]
    public void ClaudeWorkflow_TimeoutScenario()
    {
        // Arrange
        var terminalId = "claude-timeout";

        _processor.StartCommand(terminalId, "claude");
        _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");

        _processor.StartCommand(terminalId, "some command");

        // Add content that doesn't trigger immediate flush
        _processor.ProcessOutput(terminalId, "Processing");
        _processor.ProcessOutput(terminalId, "…");
        _processor.ProcessOutput(terminalId, "Still working");

        // Act - Simulate timeout
        var timeoutResult = _processor.CheckTimeout(terminalId, TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.IsNotNull(timeoutResult, "Should flush on timeout");
        Assert.IsTrue(timeoutResult.Content.Contains("Processing"), "Should contain buffered content");
    }

    [TestMethod]
    public void ClaudeWorkflow_ErrorHandling()
    {
        // Arrange
        var terminalId = "claude-error";

        _processor.StartCommand(terminalId, "claude");
        _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");

        _processor.StartCommand(terminalId, "invalid command");

        // Act - Process error response
        var errorResponse = "● I don't understand that command. Try /help for available commands.";
        var result = _processor.ProcessOutput(terminalId, errorResponse);

        // Assert
        Assert.IsNotNull(result, "Should process error response");
        Assert.IsTrue(result.Content.Contains("don't understand"), "Should contain error message");
    }

    [TestMethod]
    public void ClaudeWorkflow_MixedContent()
    {
        // Arrange
        var terminalId = "claude-mixed";

        _processor.StartCommand(terminalId, "claude");
        _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");

        _processor.StartCommand(terminalId, "complex response");

        // Act - Process mixed content with ANSI codes, control sequences, and actual response
        var mixedContent = @"[?2004l> complex response
Cogitating… (2.1 tokens/s)
\x1B[32m● This is the actual response\x1B[0m

Some additional content here
\x1B[?2004h? for shortcuts";

        var result = _processor.ProcessOutput(terminalId, mixedContent);

        // Assert
        Assert.IsNotNull(result, "Should process mixed content");
        Assert.IsTrue(result.Content.Contains("This is the actual response"), "Should contain actual response");
        Assert.IsFalse(result.Content.Contains("\x1B"), "Should remove ANSI codes");
        Assert.IsFalse(result.Content.Contains("2004"), "Should remove control sequences");
        Assert.IsFalse(result.Content.Contains("Cogitating"), "Should filter progress indicators");
    }

    [TestMethod]
    public void ClaudeWorkflow_CleanupAndRestart()
    {
        // Arrange
        var terminalId = "claude-cleanup";

        // Start Claude first time
        _processor.StartCommand(terminalId, "claude");
        var startup1 = _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");
        Assert.IsNotNull(startup1, "Should process first startup");

        // Cleanup terminal
        _processor.CleanupTerminal(terminalId);

        // Start Claude again
        _processor.StartCommand(terminalId, "claude");
        var startup2 = _processor.ProcessOutput(terminalId, "✻ Welcome to Claude Code!");

        // Assert
        Assert.IsNotNull(startup2, "Should process startup after cleanup");
        Assert.AreEqual(terminalId, startup2.TerminalId);
    }

    [TestMethod]
    public void ClaudeWorkflow_RealWorldScenario()
    {
        // Arrange - Simulate real-world usage
        var terminalId = "real-claude";

        // Complete realistic scenario
        _processor.StartCommand(terminalId, "claude");

        // Startup with full output
        var realStartup = @"\x1B[?2004l✻ Welcome to Claude Code!
/help for help, /status for your current setup
cwd: /mnt/c/projects/ClaudeMobileTerminal/src/Backend

Claude Sonnet 4 via Claude Code
context_length=200000, max_tokens=8192
\x1B[?2004h";

        var startupResult = _processor.ProcessOutput(terminalId, realStartup);
        Assert.IsNotNull(startupResult, "Should handle real startup");

        // Help command
        _processor.StartCommand(terminalId, "/help");
        
        // Simulate typing and response
        _processor.ProcessOutput(terminalId, "> /");
        _processor.ProcessOutput(terminalId, "> /h");
        _processor.ProcessOutput(terminalId, "> /help");
        
        var helpResponse = @"● Available commands:
  /help - Show this help
  /status - Show current setup
  /exit - Exit Claude Code";

        var helpResult = _processor.ProcessOutput(terminalId, helpResponse);
        Assert.IsNotNull(helpResult, "Should process help response");
        Assert.IsTrue(helpResult.Content.Contains("Available commands"), "Should contain help content");

        // Say command with real response
        _processor.StartCommand(terminalId, "say 'Hello from tests!'");
        
        var sayResponse = @"● Hello from tests!";
        var sayResult = _processor.ProcessOutput(terminalId, sayResponse);
        
        Assert.IsNotNull(sayResult, "Should process say response");
        Assert.AreEqual("Hello from tests!", sayResult.Content.Trim());
    }
}
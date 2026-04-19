using Pulse.Data;
using Pulse.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Collections.Concurrent;

namespace Pulse.Services;

public sealed record DisplayMessage(string Role, string Content, DateTime Timestamp);

/// <summary>Summary of one conversation session, used to populate the sidebar.</summary>
public sealed record SessionSummary(string SessionId, DateTime CreatedAt, DateTime LastAt, string FirstUserMessage);

/// <summary>
/// Singleton service that manages per-user, per-session chat histories.
/// Display messages are persisted to the database, filtered by both
/// <c>SessionId</c> and <c>UserId</c> so sessions are fully isolated between users.
/// </summary>
public sealed class ChatHistoryService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private sealed record InMemorySession(ChatHistory SkHistory, SemaphoreSlim Lock);

    private readonly ConcurrentDictionary<string, InMemorySession> _sessions = new();

    private const string SystemPrompt =
        "You are an intelligent AI agent for Pulse — a business intelligence and analytics platform. " +
        "You help users analyze data, generate insights, complete multi-step business tasks, and answer complex questions. " +
        "For complex tasks, break them into clear numbered steps and work through them systematically, " +
        "reporting your progress after each step. " +
        "When you need additional information from the user before you can proceed, indicate this clearly with " +
        "**[INPUT NEEDED]** followed by a specific question. " +
        "Always be professional, concise, and actionable.";

    // ── SK history (in-memory) ────────────────────────────────────────────────

    private InMemorySession GetOrCreateSession(string sessionId) =>
        _sessions.GetOrAdd(sessionId, _ => new InMemorySession(
            new ChatHistory(SystemPrompt),
            new SemaphoreSlim(1, 1)));

    public ChatHistory GetSkHistory(string sessionId) =>
        GetOrCreateSession(sessionId).SkHistory;

    public SemaphoreSlim GetSessionLock(string sessionId) =>
        GetOrCreateSession(sessionId).Lock;

    // ── Display messages (DB-backed, per-user) ────────────────────────────────

    /// <summary>
    /// Persists a display message scoped to the given user and session.
    /// Role is one of: <c>user</c>, <c>assistant</c>, <c>chart</c>.
    /// </summary>
    public void AddDisplayMessage(string sessionId, string userId, string role, string content)
    {
        using var db = dbFactory.CreateDbContext();
        db.ChatMessages.Add(new ChatMessageEntity
        {
            SessionId = sessionId,
            UserId    = userId,
            Role      = role,
            Content   = content,
            Timestamp = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>Returns all persisted display messages for the given user + session.</summary>
    public IReadOnlyList<DisplayMessage> GetDisplayMessages(string sessionId, string userId)
    {
        using var db = dbFactory.CreateDbContext();
        return db.ChatMessages
            .Where(m => m.SessionId == sessionId && m.UserId == userId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new DisplayMessage(m.Role, m.Content, m.Timestamp))
            .ToList();
    }

    /// <summary>
    /// Returns a summary of every session the user has, ordered most-recent first.
    /// Used to populate the conversations sidebar.
    /// </summary>
    public IReadOnlyList<SessionSummary> GetUserSessions(string userId)
    {
        using var db = dbFactory.CreateDbContext();

        // Project to a lightweight DTO first so EF generates a simple SELECT,
        // then group in memory — GroupBy with nested subqueries can't be translated to SQL.
        var rows = db.ChatMessages
            .Where(m => m.UserId == userId)
            .Select(m => new { m.SessionId, m.Role, m.Content, m.Timestamp })
            .ToList();

        return rows
            .GroupBy(m => m.SessionId)
            .Select(g => new SessionSummary(
                g.Key,
                g.Min(m => m.Timestamp),
                g.Max(m => m.Timestamp),
                g.Where(m => m.Role == "user")
                 .OrderBy(m => m.Timestamp)
                 .Select(m => m.Content)
                 .FirstOrDefault() ?? "New conversation"))
            .OrderByDescending(s => s.LastAt)
            .ToList();
    }

    /// <summary>
    /// Removes the in-memory session and deletes only that user's messages for the session.
    /// </summary>
    public void RemoveSession(string sessionId, string userId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            session.Lock.Dispose();

        using var db = dbFactory.CreateDbContext();
        db.ChatMessages
            .Where(m => m.SessionId == sessionId && m.UserId == userId)
            .ExecuteDelete();
    }

    public IReadOnlyCollection<string> ActiveSessionIds => _sessions.Keys.ToList();

    // ── Active-session status (thinking | responding) ─────────────────────────

    private readonly ConcurrentDictionary<string, string> _statuses = new();

    public void   SetSessionStatus(string sessionId, string status) => _statuses[sessionId] = status;
    public void   ClearSessionStatus(string sessionId)               => _statuses.TryRemove(sessionId, out _);
    public string? GetSessionStatus(string sessionId)                 =>
        _statuses.TryGetValue(sessionId, out var s) ? s : null;
}


/// <summary>
/// Singleton service that manages per-session chat histories.
///
/// Display messages (user / assistant / chart) are persisted to the database via
/// <see cref="IDbContextFactory{ApplicationDbContext}

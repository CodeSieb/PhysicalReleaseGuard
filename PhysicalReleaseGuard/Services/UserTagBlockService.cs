using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

/// <summary>
/// Background service that monitors for newly created users and automatically
/// adds the plugin's configured tag to their BlockedTags in Parental Control.
/// </summary>
public class UserTagBlockService : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly ILogger<UserTagBlockService> _logger;

    // Track user IDs we've already seen/processed to detect new users.
    private readonly ConcurrentDictionary<Guid, bool> _knownUserIds = new();

    // Poll interval for checking new users.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    public UserTagBlockService(
        IUserManager userManager,
        ILogger<UserTagBlockService> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Seed the known user set with all current users.
        foreach (var user in _userManager.GetUsers())
        {
            _knownUserIds.TryAdd(user.Id, true);
        }

        _logger.LogInformation(
            "UserTagBlockService initialized. Tracking {Count} existing users.", _knownUserIds.Count);

        _pollTask = PollForNewUsersAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task PollForNewUsersAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CheckForNewUsersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for new users.");
            }
        }
    }

    private async Task CheckForNewUsersAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.AutoBlockTagForNewUsers)
        {
            return;
        }

        var tagName = config.TagName;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            tagName = "Hidden";
        }

        var allUsers = _userManager.GetUsers().ToList();

        foreach (var user in allUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_knownUserIds.TryAdd(user.Id, true))
            {
                continue; // Already known
            }

            // New user detected
            _logger.LogInformation("New user detected: {UserName} ({UserId}). Applying blocked tag '{TagName}'.",
                user.Username, user.Id, tagName);

            try
            {
                await ApplyBlockedTagAsync(user.Id, tagName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply blocked tag '{TagName}' to new user '{UserName}'.",
                    tagName, user.Username);
            }
        }
    }

    /// <summary>
    /// Applies the configured tag to the BlockedTags of a single user.
    /// </summary>
    public async Task ApplyBlockedTagAsync(Guid userId, string tagName, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.LogWarning("Cannot apply blocked tag: user {UserId} not found.", userId);
            return;
        }

        // Read the current policy via GetUserDto which includes the full UserPolicy.
        var userDto = _userManager.GetUserDto(user, string.Empty);
        var policy = userDto.Policy;
        if (policy == null)
        {
            _logger.LogWarning("Cannot apply blocked tag: user '{UserName}' has no policy.", user.Username);
            return;
        }

        var blockedTags = policy.BlockedTags ?? Array.Empty<string>();

        if (blockedTags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("User '{UserName}' already has tag '{TagName}' blocked.", user.Username, tagName);
            return;
        }

        policy.BlockedTags = blockedTags.Concat(new[] { tagName }).ToArray();
        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);

        _logger.LogInformation("Added '{TagName}' to BlockedTags for user '{UserName}'.", tagName, user.Username);
    }

    /// <summary>
    /// Applies the configured tag to the BlockedTags of ALL existing users who don't already have it.
    /// Returns the number of users modified.
    /// </summary>
    public async Task<int> ApplyBlockedTagToAllUsersAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        var tagName = config?.TagName;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            tagName = "Hidden";
        }

        var allUsers = _userManager.GetUsers().ToList();
        var modified = 0;

        foreach (var user in allUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var userDto = _userManager.GetUserDto(user, string.Empty);
            var policy = userDto.Policy;
            if (policy == null)
            {
                continue;
            }

            var blockedTags = policy.BlockedTags ?? Array.Empty<string>();
            if (blockedTags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                policy.BlockedTags = blockedTags.Concat(new[] { tagName }).ToArray();
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                modified++;

                _logger.LogInformation("Added '{TagName}' to BlockedTags for user '{UserName}'.", tagName, user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply blocked tag to user '{UserName}'.", user.Username);
            }
        }

        _logger.LogInformation("ApplyBlockedTagToAllUsers complete. Modified {Modified} of {Total} users.",
            modified, allUsers.Count);

        return modified;
    }
}

# playnite-friendsachievementfeed-plugin

Small [Playnite ](https://playnite.link/)extension that shows a chronological feed of your friends' achievement activity.

## Quick overview

- Global feed: scrollable list of friends' recent unlocks.
- Per-game feed: integrated Game view tab and a game-specific window.
- Filters: limit the feed to specific friends or games.
- Notifications: optional toasts for rebuilds and periodic updates.

## Design highlights

- Caching: the extension builds and maintains a local JSON cache of feed entries to keep the UI fast and avoid repeated API calls.
- Two rebuild modes:
  - Full rebuild: scans friends and owned games and reconstructs the entire cache.
  - Incremental rebuild: quick checks for recent changes and merges new entries into the existing cache.
- Background updates: optional periodic incremental updates run on a configurable schedule.
- SuccessStory integration: imports local SuccessStory files (if available) to include metadata not available from the Steam API.

## Types of feeds

- Global feed — combined activity across all games and friends.
- Game feed — per-game grouped view (theme-friendly tab and a resizable window).
- Friend groups — feed entries are grouped by friend and date for readability.

## Configuration (summary)

- Rebuild controls: trigger manual full or incremental rebuilds from the feed UI.
- Periodic updates: enable/disable and set interval hours.
- Notifications: toggle toasts for rebuilds and periodic updates.
- Visual options: avatar/icon sizes and feed item limits.

## Troubleshooting

- No feed items: confirm Steam API key and user ID are configured in plugin settings and that SuccessStory files (if used) are accessible.
- Rebuild failures: check plugin logs (Playnite logs) for detailed errors and try a manual full rebuild.

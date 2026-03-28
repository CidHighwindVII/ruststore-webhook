# Rust Store Webhook

A Windows utility that automatically posts the weekly Rust item store listings to a Discord channel via webhook. It scrapes the [Rust Steam item store](https://store.steampowered.com/itemstore/252490/) and sends each item as a rich Discord embed — name, price, thumbnail, and a direct link.

## What it does

On the configured day and time, the tool:

1. Fetches the current item list from the Rust Steam store
2. Sends a header embed to Discord with the total item count
3. Posts each item as an individual embed (name, price, image, link)

If the machine was off on the scheduled day, the tool catches up automatically at the next login.

## Requirements

- **Windows 10/11 x64** — the exe is self-contained, no .NET install needed
- A **Discord webhook URL** for the channel you want to post to

## Setup

### 1. Create a Discord webhook

1. Open your Discord server settings > Integrations > Webhooks
2. Click **New Webhook**, choose the target channel, copy the URL

### 2. Configure the `.env` file

Create a `.env` file next to the exe:

```
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/YOUR_ID/YOUR_TOKEN
SEND_DAY=THU
SEND_TIME=22:00
```

| Key | Required | Values | Default |
|-----|----------|--------|---------|
| `DISCORD_WEBHOOK_URL` | Yes | Your Discord webhook URL | — |
| `SEND_DAY` | No | `MON` `TUE` `WED` `THU` `FRI` `SAT` `SUN` | `THU` |
| `SEND_TIME` | No | 24-hour time, e.g. `22:00` | `22:00` |

### 3. Register the scheduled tasks (run once)

Open a terminal in the folder containing the exe and run:

```
.\ruststore-webhook.exe --setup
```

This registers two Windows Task Scheduler tasks using the day and time from your `.env`:

| Task | Trigger |
|------|---------|
| `RustStoreWebhook` | Every configured day at the configured time |
| `RustStoreWebhook_Startup` | At every login (missed-day catch-up) |

> If you change `SEND_DAY` or `SEND_TIME` later, re-run `--setup` to update the tasks.

## Manual run

To run immediately without waiting for the schedule:

```
.\ruststore-webhook.exe
```

The tool will skip execution if it has already run for the current week's scheduled day.

## Files created at runtime

| File | Purpose |
|------|---------|
| `last_run.txt` | Tracks the last date the webhook ran (prevents duplicate posts) |
| `ruststore.log` | Timestamped log of every run |

Both files are created next to the exe automatically.

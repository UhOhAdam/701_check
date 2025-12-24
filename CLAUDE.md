# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a C# .NET 10.0 console application called "701_check" that monitors South Western Railway (SWR) train fleet status. The application scrapes real-time train data from realtimetrains.co.uk and posts formatted reports to Discord via webhooks.

## Core Architecture

### Main Components
- **Program.cs**: Single-file application containing all logic
- **SWR701Tracker namespace**: Contains the main Program class with static methods
- **Data Sources**: Scrapes HTML from realtimetrains.co.uk using HtmlAgilityPack
- **Output**: Sends formatted reports to Discord webhook

### Key Functionality
- **Check701Async()**: Monitors Class 701 trains (units 701001-701060)
- **Check458Async()**: Monitors Class 458/5 trains (active units only)
- **FetchHeadcodeAndIdentities()**: Extracts service details from HTML
- **NotifyDiscord()**: Formats and sends fleet status reports
- **SquashReversal()**: Handles duplicate unit formations

### Data Flow
1. Concurrent HTTP requests to search for each unit
2. Follow redirects to service pages for active trains
3. Parse HTML to extract headcodes, identities, and reversal stations
4. Classify services (in_service/testing/depot) based on headcodes
5. Group by railway lines and format for Discord

## Commands

### Build and Run
```bash
# Restore dependencies
dotnet restore

# Build (Debug)
dotnet build

# Build (Release)
dotnet build --configuration Release

# Run application
dotnet run

# Run built executable (Release)
./bin/Release/net10.0/701_check.exe
```

### Dependencies
- .NET 10.0
- HtmlAgilityPack 1.12.1 (for HTML parsing)
- System.Text.Json (built-in, for Discord webhook payloads)

## Environment Variables

### Required
- **DISCORD_WEBHOOK**: Discord webhook URL for posting reports (required at runtime)

## Configuration Data

### Headcode Mappings
The application contains hardcoded mappings of 2-character headcodes to SWR railway lines:
- Windsor line: 2U, 1U
- Hampton Court: 1J, 2J
- Shepperton: 2H, 1H
- Reading: 2C, 1C
- And others...

### Active Units
- **Class 701**: Checks units 701001 through 701060 (60 units total)
- **Class 458/5**: Only monitors predefined active units (458529, 458530, 458533, 458535, 458536)

## Automation

### GitHub Actions
The repository includes automated scheduled execution via `.github/workflows/701_check.yml`:
- Runs every 30 minutes from 4:00-22:30 GMT
- Uses Windows runners with .NET 10.0
- Requires DISCORD_WEBHOOK secret to be configured in repository

### Build Process
1. Checkout code
2. Setup .NET 10.0
3. Restore dependencies
4. Build in Release configuration
5. Execute with environment variables

## Development Notes

### HTTP Handling
- Uses HttpClientHandler with AllowAutoRedirect = false to detect service redirects
- Implements proper disposal patterns for HttpClient instances
- Handles both 302 redirects and direct responses

### Error Handling
- Try-catch blocks around all HTTP operations
- Graceful degradation when units cannot be found
- Console logging for debugging errors

### Data Processing
- Concurrent Task.WhenAll() for performance when checking multiple units
- HashSet tracking to avoid duplicate 458/5 unit processing
- Special handling for mirror formations in multi-unit trains
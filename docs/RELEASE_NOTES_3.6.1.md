# Lurp 3.6.1

3.6.1 fixes the release workflow, which gave up waiting for nuget.org to index
3.6.0 after 15 minutes and failed the publish job although the package was
live. There are no library, CLI, MCP or database changes. No index run is
needed.

## Upgrade from 3.6.0

Install 3.6.1. The extractor version, the database schema, the CLI/MCP
contract and the output schema do not change.

## Changes

### Release workflow

The step that verifies the published nuget.org catalog entry now waits up to
60 minutes instead of 15, and the publish job limit is 90 minutes instead of
30. nuget.org took about 16.5 minutes to index 3.6.0.

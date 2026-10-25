# Collaboration sessions and per-ID locks

Open the WASM frontend and enter a display name. This creates a distinct
collaboration session for that tab. Names identify editors but are not verified
accounts; authentication and authorization are deferred. There is no shared
implicit local user and no removed server UI to launch.

Select a backend-configured library and click **Open library**. Only library IDs
configured under `Assets:Libraries` can be opened. The backend owns source file
handles, private drafts and published versions; browsers receive bounded data.

## Viewing and editing

Selecting Gump 1 is read-only and does not lock it. **Start editing ID** acquires
its lease. Alice can still open Gump 1 and see the last published image, together
with **Locked by <your name>**. Her mutation controls are disabled and direct API
writes are rejected by the backend. Private drafts become visible to other
sessions only after publication and refresh.

Lock keys distinguish source library, resource kind and ID. Land 10 and Item 10
are independent slots even though they share art.mul. Gump 10 is independent.
The same file exposed under directory aliases normalizes to a shared lock key.
Font, hue, Cliloc and TileData editors also enforce ownership by filename,
record kind and ID. Font glyph IDs combine font index and character code.

## Lifetime and publishing

The interface polls owner metadata every three seconds and renews active
workspace leases. The lock expires after two minutes without renewal. Explicit
**Release ID** or **End session** releases clean locks immediately. Drafts must
be published or explicitly discarded before releasing their ID. Browser crashes
and lost connections rely on expiry; closing a tab is not a reliable cleanup
request. Sessions expire after 15 minutes without activity.

Publishing pins the caller's leases, checks each record's base version and
merges modifications against the latest published archive. Different designers
can publish different IDs without discarding each other's changes. An editor
whose lease expired cannot write through the new owner's lock, release it or
overwrite a newer published record. Its private draft remains until discarded.

Published files and manifests are kept in backend-managed App_Data storage;
original source files are unchanged. Run one backend instance per source
library. Multiple replicas require a distributed lock/version coordinator.

See [API reference](http-api.md), [split architecture](split-architecture.md)
and the [README](../README.md) for startup and configuration.

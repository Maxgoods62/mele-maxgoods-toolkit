#pragma once

// Identity the ASI loader (SPI) logs and checks; same macro names as the LExASIs projects.

#define DEVELOPER_A "Maxgoods"
#define DEVELOPER_W L"Maxgoods"
#define ASI_NAME_A "Mele Bridge"
#define ASI_NAME_W L"Mele Bridge"
#define ASI_NAME_NO_SPACE_A "MeleBridge"
#define ASI_NAME_NO_SPACE_W L"MeleBridge"

#define ASI_VERSION 4
#define VERSION_STRING_A "4"
#define VERSION_STRING_W L"4"

// The pipe protocol: ping returns it, and mele-mcp refuses a bridge with another number. Bump it when an op changes
// its arguments or its reply. A new op needs no bump: an older bridge answers it "unknown op" (MeleBridge 3 added
// status and keep_ticking under protocol 1).
#define MELE_PROTOCOL_VERSION 1

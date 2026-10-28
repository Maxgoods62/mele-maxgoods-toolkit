#pragma once

// Identity the ASI loader (SPI) logs and checks; same macro names as the LExASIs projects.

#define DEVELOPER_A "Maxgoods"
#define DEVELOPER_W L"Maxgoods"
#define ASI_NAME_A "Mele Bridge"
#define ASI_NAME_W L"Mele Bridge"
#define ASI_NAME_NO_SPACE_A "MeleBridge"
#define ASI_NAME_NO_SPACE_W L"MeleBridge"

#define ASI_VERSION 2
#define VERSION_STRING_A "2"
#define VERSION_STRING_W L"2"

// The pipe protocol: ping returns it, and mele-mcp refuses a bridge with another number. Bump it when an op changes
// its arguments or its reply.
#define MELE_PROTOCOL_VERSION 1

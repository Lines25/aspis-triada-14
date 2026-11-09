// v48.1 compatibility fix:
// The aspisFactionExpeditionSite prototype must be defined in Content.Shared
// because Resources/Prototypes/_Aspis/Organizations/faction_expedition_sites.yml
// is loaded by both the client and the server.
//
// The real class now lives in:
// Content.Shared/_Aspis/Organizations/AspisFactionExpeditionSitePrototype.cs
// Keep this file free of [Prototype] to prevent duplicate type registration.

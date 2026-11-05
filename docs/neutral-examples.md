# Neutral examples and current setup instructions

Use generic OIDC registration steps and example.com hosts rather than provider-specific
console walkthroughs. The backend validates JWT access tokens; opaque tokens remain
unsupported. Keep agent names, wire protocol names and required integration identifiers
because changing them would misdescribe supported configuration or break delivery.

Rejected: renaming protocol/provider enum values would change API contracts. Removing the
access-token limitation with the walkthroughs would imply unsupported identity setups work.
The license service URL remains the project endpoint: this change only neutralizes examples
and comments and corrects the Admin activation and chat-message descriptions.

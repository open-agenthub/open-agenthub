using AgentHub.Api.Persistence;
    sealed class Store : ISessionStore
    {
        public int Lookups;
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            Lookups++;
            return Task.FromResult<SessionRecord?>(new SessionRecord { Id = id, Owner = "owner", CallbackToken = "proof251" });
        }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => GetByIdAsync(id, ct);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => token == "proof251" ? GetByIdAsync("s251", ct) : Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListExpiredAsync(DateTime now, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }


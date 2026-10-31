namespace Te1000Daemon
{
    // Who is asking and for what, as the dispatcher knows it. Today the principal is a
    // CLAIM made by the client over the pipe, which is why AuthLevel says "none": the audit
    // must never present an unverified name as an identity.
    public sealed class AccessRequest
    {
        public string Action;
        public Json.JObj Payload;
        public string ClientKind;
        public string Principal;
    }

    public sealed class AccessDecision
    {
        public bool Allowed;
        public string AuthLevel;
        public string Reason;
    }

    // The one point every call passes before it runs. Authentication and timed grants plug
    // in here without touching the dispatcher or the audit format.
    public interface IAccessPolicy
    {
        AccessDecision Check(AccessRequest request);
    }

    // Nothing is authenticated yet: this says so in every audit row instead of omitting it.
    public sealed class AllowAllPolicy : IAccessPolicy
    {
        public AccessDecision Check(AccessRequest request)
        {
            return new AccessDecision { Allowed = true, AuthLevel = "none", Reason = "no access policy configured" };
        }
    }
}

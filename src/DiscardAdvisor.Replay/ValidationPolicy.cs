namespace DiscardAdvisor.Replay;

public static class ValidationPolicy
{
    public const int RequiredShadowGameCount = 5;
    public const bool ExpertAnnotationsRequired = false;
    public const int OptionalExpertAnnotationTarget = 200;
    public const double OptionalExpertTop3ConsistencyTarget = 0.8d;
}

namespace SafeVideoTransfer.Models;

public readonly record struct TransferProgress(long BytesSent, long TotalBytes)
{
	public double Fraction => TotalBytes == 0 ? 0 : (double)BytesSent / TotalBytes;
}

public sealed record VideoRecordingResult(TimeSpan Duration);
public sealed record VerificationResult(bool IsVerified, long? RemoteLength, string Message);

using System.IO;

namespace JanusClearingEngine.SignatureVerifiers;

public interface IFileSignatureVerifier
{
    void VerifySignature(Stream fileStream, byte[] signature);
}

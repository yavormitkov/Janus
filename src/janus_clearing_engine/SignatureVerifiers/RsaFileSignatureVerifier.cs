using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace JanusClearingEngine.SignatureVerifiers;

public sealed class RsaFileSignatureVerifier : IFileSignatureVerifier
{
    private readonly string _publicKeyXml;

    public RsaFileSignatureVerifier(IConfiguration configuration)
    {
        var pem = configuration["EasyPayConfiguration:PublicKeyXml"]
            ?? throw new InvalidOperationException("Missing PublicKeyXml configuration.");

        _publicKeyXml = pem.Replace("\\n", "\n").Replace("\\r", "").Trim();
    }

    public void VerifySignature(Stream fileStream, byte[] signature)
    {
        using var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(_publicKeyXml.ToCharArray());
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException("Invalid cryptographic PEM public key structure.", ex);
        }

        bool isValid = rsa.VerifyData(fileStream, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        if (!isValid)
        {
            throw new SecurityException("Cryptographic signature breach validation failed.");
        }
    }
}

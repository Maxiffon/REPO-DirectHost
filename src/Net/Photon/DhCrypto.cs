// REPO DirectHost — Photon payload encryption key exchange (server side).
// Uses the game's own DiffieHellmanCryptoProvider (Photon3Unity3D) so keys are guaranteed compatible.
using System;
using System.Reflection;

namespace RepoDirectHost.Net.Photon
{
    internal sealed class DhCrypto
    {
        private static readonly Type T;
        private static readonly PropertyInfo PublicKeyProp;
        private static readonly MethodInfo DeriveM, DecryptM, EncryptM;
        public static readonly string InitError;

        static DhCrypto()
        {
            try
            {
                T = typeof(ExitGames.Client.Photon.PhotonPeer).Assembly.GetType("Photon.SocketServer.Security.DiffieHellmanCryptoProvider", true);
                PublicKeyProp = T.GetProperty("PublicKey");
                DeriveM = T.GetMethod("DeriveSharedKey", new[] { typeof(byte[]) });
                DecryptM = T.GetMethod("Decrypt", new[] { typeof(byte[]), typeof(int), typeof(int) });
                EncryptM = T.GetMethod("Encrypt", new[] { typeof(byte[]), typeof(int), typeof(int) });
                if (PublicKeyProp == null || DeriveM == null || DecryptM == null) InitError = "DiffieHellmanCryptoProvider members not found";
            }
            catch (Exception e) { InitError = e.Message; }
        }

        private readonly object _o;
        public bool Ready { get; private set; }

        public DhCrypto()
        {
            if (InitError != null) throw new InvalidOperationException("Photon crypto unavailable: " + InitError);
            _o = Activator.CreateInstance(T);
        }

        public byte[] PublicKey => (byte[])PublicKeyProp.GetValue(_o);
        public void Derive(byte[] clientPublicKey) { DeriveM.Invoke(_o, new object[] { clientPublicKey }); Ready = true; }
        public byte[] Decrypt(byte[] data, int offset, int count) => (byte[])DecryptM.Invoke(_o, new object[] { data, offset, count });
        public byte[] Encrypt(byte[] data, int offset, int count) => (byte[])EncryptM.Invoke(_o, new object[] { data, offset, count });
    }
}

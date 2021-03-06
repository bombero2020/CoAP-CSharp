﻿using System;
using System.Collections;

using System.Threading.Tasks;
using Com.AugustCellars.COSE;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Utilities.Encoders;
using Org.BouncyCastle.X509;
using PeterO.Cbor;


namespace Com.AugustCellars.CoAP.DTLS
{

    class DtlsClient : DefaultTlsClient
    {
        private TlsPskIdentity _mPskIdentity;
        private TlsSession _mSession;
        public EventHandler<TlsEvent> TlsEventHandler;
        public OneKey _rawPublicKey;

        internal DtlsClient(TlsSession session, TlsPskIdentity pskIdentity)
        {
            _mSession = session;
            _mPskIdentity = pskIdentity;
        }

        internal DtlsClient(TlsSession session, OneKey userKey)
        {
            _mSession = session;
            _rawPublicKey = userKey;
        }

        private void OnTlsEvent(Object o, TlsEvent e)
        {
            EventHandler<TlsEvent> handler = TlsEventHandler;
            if (handler != null) {
                handler(o, e);
            }
        }

        public override ProtocolVersion MinimumVersion => ProtocolVersion.DTLSv10;

        public override ProtocolVersion ClientVersion => ProtocolVersion.DTLSv12;

        public override int[] GetCipherSuites()
        {
            int[] i;

            if (_rawPublicKey != null) {
                i = new int[] {
                    CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8,
                    CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CCM_8
                };
            }
            else {
                i = new int[] {
                    CipherSuite.TLS_PSK_WITH_AES_128_CCM_8,
                };
            }

            TlsEvent e = new TlsEvent(TlsEvent.EventCode.GetCipherSuites) {
                IntValues = i
            };

            EventHandler<TlsEvent> handler = TlsEventHandler;
            if (handler != null) {
                handler(this, e);
            }

            return e.IntValues;
        }


        public override IDictionary GetClientExtensions()
        {
            IDictionary clientExtensions = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetClientExtensions());


            // TlsExtensionsUtilities.AddEncryptThenMacExtension(clientExtensions);
            // TlsExtensionsUtilities.AddExtendedMasterSecretExtension(clientExtensions);
            {
                /*
                 * NOTE: If you are copying test code, do not blindly set these extensions in your own client.
                 */
            //   TlsExtensionsUtilities.AddMaxFragmentLengthExtension(clientExtensions, MaxFragmentLength.pow2_9);
            //    TlsExtensionsUtilities.AddPaddingExtension(clientExtensions, mContext.SecureRandom.Next(16));
            //    TlsExtensionsUtilities.AddTruncatedHMacExtension(clientExtensions);

                if (_rawPublicKey != null) {
                    TlsExtensionsUtilities.AddClientCertificateTypeExtensionClient(clientExtensions, new byte[]{2});
                    TlsExtensionsUtilities.AddServerCertificateTypeExtensionClient(clientExtensions, new byte[]{2});
                }
            }

            TlsEvent e = new TlsEvent(TlsEvent.EventCode.GetExtensions) {
                Dictionary = clientExtensions
            };


            EventHandler<TlsEvent> handler = TlsEventHandler;
            if (handler != null) {
                handler(this, e);
            }

            return e.Dictionary;
        }

        public override TlsAuthentication GetAuthentication()
        {
            MyTlsAuthentication auth = new MyTlsAuthentication(mContext, _rawPublicKey);
            auth.TlsEventHandler += MyTlsEventHandler;
            return auth;
        }

        private void MyTlsEventHandler(object sender, TlsEvent tlsEvent)
        {
            EventHandler<TlsEvent> handler = TlsEventHandler;
            if (handler != null) {
                handler(sender, tlsEvent);
            }
        }

        public override TlsKeyExchange GetKeyExchange()
        {
            int keyExchangeAlgorithm = TlsUtilities.GetKeyExchangeAlgorithm(mSelectedCipherSuite);

            switch (keyExchangeAlgorithm) {
            case KeyExchangeAlgorithm.DHE_PSK:
            case KeyExchangeAlgorithm.ECDHE_PSK:
            case KeyExchangeAlgorithm.PSK:
            case KeyExchangeAlgorithm.RSA_PSK:
                return CreatePskKeyExchange(keyExchangeAlgorithm);

            case KeyExchangeAlgorithm.ECDH_anon:
            case KeyExchangeAlgorithm.ECDH_ECDSA:
            case KeyExchangeAlgorithm.ECDH_RSA:
                return CreateECDHKeyExchange(keyExchangeAlgorithm);

            case KeyExchangeAlgorithm.ECDHE_ECDSA:
                return CreateECDheKeyExchange(keyExchangeAlgorithm);

            default:
                /*
                    * Note: internal error here; the TlsProtocol implementation verifies that the
                    * server-selected cipher suite was in the list of client-offered cipher suites, so if
                    * we now can't produce an implementation, we shouldn't have offered it!
                    */
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        private BigInteger ConvertBigNum(CBORObject cbor)
        {
            byte[] rgb = cbor.GetByteString();
            byte[] rgb2 = new byte[rgb.Length + 2];
            rgb2[0] = 0;
            rgb2[1] = 0;
            for (int i = 0; i < rgb.Length; i++) rgb2[i + 2] = rgb[i];

            return new BigInteger(rgb2);
        }

        protected TlsSignerCredentials GetECDsaSignerCredentials()
        {
#if SUPPORT_RPK
            if (_rawPublicKey != null) {
                OneKey k = _rawPublicKey;
                if (k.HasKeyType((int)COSE.GeneralValuesInt.KeyType_EC2) &&
                    k.HasAlgorithm(COSE.AlgorithmValues.ECDSA_256)) {

                    X9ECParameters p = k.GetCurve();
                    ECDomainParameters parameters = new ECDomainParameters(p.Curve, p.G, p.N, p.H);
                    ECPrivateKeyParameters privKey = new ECPrivateKeyParameters("ECDSA", ConvertBigNum(k[CoseKeyParameterKeys.EC_D]), parameters);

                    ECPoint point = k.GetPoint();
                    ECPublicKeyParameters param = new ECPublicKeyParameters(point, parameters);

                    SubjectPublicKeyInfo spi = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(param);

                    return new DefaultTlsSignerCredentials(mContext, new RawPublicKey(spi), privKey, new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
                }
            }
#endif

            TlsEvent e = new TlsEvent(TlsEvent.EventCode.SignCredentials) {
                CipherSuite = KeyExchangeAlgorithm.ECDH_ECDSA
            };

            EventHandler<TlsEvent> handler = TlsEventHandler;
            if (handler != null) {
                handler(this, e);
            }

            if (e.SignerCredentials != null) return e.SignerCredentials;
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        /// <summary>
        /// We don't care if we cannot do secure renegotiation at this time.
        /// This needs to be reviewed in the future M00TODO
        /// </summary>
        /// <param name="secureRenegotiation"></param>
        public override void NotifySecureRenegotiation(bool secureRenegotiation)
        {
            //  M00TODO - should we care?
        }


        internal class MyTlsAuthentication
            : TlsAuthentication
        {
            private readonly TlsContext _mContext;
            public EventHandler<TlsEvent> TlsEventHandler;
            private OneKey _rawPublicKey;
            private KeySet _serverKeys;

            internal MyTlsAuthentication(TlsContext context, OneKey rawPublicKey)
            {
                this._mContext = context;
                _rawPublicKey = rawPublicKey;
            }

            public OneKey AuthenticationKey { get; private set; }

#if SUPPORT_RPK

            public virtual void NotifyServerCertificate(AbstractCertificate serverCertificate)
            {
                if (serverCertificate is RawPublicKey) {
                    GetRpkKey((RawPublicKey)serverCertificate);
                }
                else {
                    TlsEvent e = new TlsEvent(TlsEvent.EventCode.ServerCertificate) {
                        Certificate = serverCertificate
                    };

                    EventHandler<TlsEvent> handler = TlsEventHandler;
                    if (handler != null) {
                        handler(this, e);
                    }

                    if (!e.Processed) {
                        throw new TlsFatalAlert(AlertDescription.certificate_unknown);
                    }
                }
            }

            public void GetRpkKey(RawPublicKey rpk)
            {
                AsymmetricKeyParameter key;

                try {
                    key = PublicKeyFactory.CreateKey(rpk.SubjectPublicKeyInfo());
                }
                catch (Exception e) {
                    throw new TlsFatalAlert(AlertDescription.unsupported_certificate, e);
                }

                if (key is ECPublicKeyParameters) {
                    ECPublicKeyParameters ecKey = (ECPublicKeyParameters)key;

                    string s = ecKey.AlgorithmName;
                    OneKey newKey = new OneKey();
                    newKey.Add(CoseKeyKeys.KeyType, GeneralValues.KeyType_EC);
                    if (ecKey.Parameters.Curve.Equals(NistNamedCurves.GetByName("P-256").Curve)) {
                        newKey.Add(CoseKeyParameterKeys.EC_Curve, GeneralValues.P256);
                    }

                    newKey.Add(CoseKeyParameterKeys.EC_X, CBORObject.FromObject(ecKey.Q.Normalize().XCoord.ToBigInteger().ToByteArrayUnsigned()));
                    newKey.Add(CoseKeyParameterKeys.EC_Y, CBORObject.FromObject(ecKey.Q.Normalize().YCoord.ToBigInteger().ToByteArrayUnsigned()));

                    if (_serverKeys != null) {
                        foreach (OneKey k in _serverKeys) {
                            if (k.Compare(newKey)) {
                                AuthenticationKey = k;
                                return;
                            }
                        }
                    }

                    TlsEvent ev = new TlsEvent(TlsEvent.EventCode.ServerCertificate) {
                        KeyValue = newKey
                    };

                    EventHandler<TlsEvent> handler = TlsEventHandler;
                    if (handler != null) {
                        handler(this, ev);
                    }

                    if (!ev.Processed) {
                        //                    throw new TlsFatalAlert(AlertDescription.certificate_unknown);
                    }
                }
                else {
                    // throw new TlsFatalAlert(AlertDescription.certificate_unknown);
                }

            }

#endif

            public virtual void NotifyServerCertificate(Certificate serverCertificate)
            {
                /*                
                                X509CertificateStructure[] chain = serverCertificate.GetCertificateList();
                                Console.WriteLine("DTLS client received server certificate chain of length " + chain.Length);
                                for (int i = 0; i != chain.Length; i++) {
                                    X509CertificateStructure entry = chain[i];
                                    // TODO Create fingerprint based on certificate signature algorithm digest
                                    Console.WriteLine("    fingerprint:SHA-256 " + TlsTestUtilities.Fingerprint(entry) + " ("
                                                      + entry.Subject + ")");
                                }
                                */
                TlsEvent e = new TlsEvent(TlsEvent.EventCode.ServerCertificate) {
                    Certificate = serverCertificate
                };

                EventHandler<TlsEvent> handler = TlsEventHandler;
                if (handler != null) {
                    handler(this, e);
                }

                if (!e.Processed) {
                    throw new TlsFatalAlert(AlertDescription.certificate_unknown);
                }
            }

            private BigInteger ConvertBigNum(CBORObject cbor)
            {
                byte[] rgb = cbor.GetByteString();
                byte[] rgb2 = new byte[rgb.Length + 2];
                rgb2[0] = 0;
                rgb2[1] = 0;
                for (int i = 0; i < rgb.Length; i++) rgb2[i + 2] = rgb[i];

                return new BigInteger(rgb2);
            }

            public virtual TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                if (certificateRequest.CertificateTypes == null ||
                    !Arrays.Contains(certificateRequest.CertificateTypes, ClientCertificateType.ecdsa_sign)) {
                    return null;
                }

#if SUPPORT_RPK
                if (_rawPublicKey != null) {
                    OneKey k = _rawPublicKey;
                    if (k.HasKeyType((int)COSE.GeneralValuesInt.KeyType_EC2) &&
                        k.HasAlgorithm(COSE.AlgorithmValues.ECDSA_256)) {

                        X9ECParameters p = k.GetCurve();
                        ECDomainParameters parameters = new ECDomainParameters(p.Curve, p.G, p.N, p.H);
                        ECPrivateKeyParameters privKey = new ECPrivateKeyParameters("ECDSA", ConvertBigNum(k[CoseKeyParameterKeys.EC_D]), parameters);

                        ECPoint point = k.GetPoint();
                        ECPublicKeyParameters param = new ECPublicKeyParameters("ECDSA", point, /*parameters*/ SecObjectIdentifiers.SecP256r1);

                        SubjectPublicKeyInfo spi = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(param);

                        return new DefaultTlsSignerCredentials(_mContext, new RawPublicKey(spi), privKey, new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
                    }

                }
                /*
                byte[] certificateTypes = certificateRequest.CertificateTypes;
                if (certificateTypes == null || !Arrays.Contains(certificateTypes, ClientCertificateType.rsa_sign))
                    return null;

                return TlsTestUtilities.LoadSignerCredentials(mContext, certificateRequest.SupportedSignatureAlgorithms,
                    SignatureAlgorithm.rsa, "x509-client.pem", "x509-client-key.pem");
                    */
#endif
                // If we did not fine appropriate signer credientials - ask for help

                TlsEvent e = new TlsEvent(TlsEvent.EventCode.SignCredentials) {
                    CipherSuite = KeyExchangeAlgorithm.ECDHE_ECDSA
                };

                EventHandler<TlsEvent> handler = TlsEventHandler;
                if (handler != null) {
                    handler(this, e);
                }

                if (e.SignerCredentials != null) return e.SignerCredentials;
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }


        };
        protected virtual TlsKeyExchange CreatePskKeyExchange(int keyExchange)
        {
            return new TlsPskKeyExchange(keyExchange, mSupportedSignatureAlgorithms, _mPskIdentity, null, null, null, mNamedCurves,
                mClientECPointFormats, mServerECPointFormats);
        }

        protected override TlsKeyExchange CreateECDHKeyExchange(int keyExchange)        {
            return new TlsECDHKeyExchange(keyExchange, mSupportedSignatureAlgorithms, mNamedCurves, mClientECPointFormats,
                mServerECPointFormats);
        }

    }
}


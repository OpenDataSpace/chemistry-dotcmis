/*
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements.  See the NOTICE file
 * distributed with this work for additional information
 * regarding copyright ownership.  The ASF licenses this file
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License.  You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Runtime.Serialization;

using DotCMIS.Enums;
using DotCMIS.Exceptions;
using DotCMIS.Util;

namespace DotCMIS.Binding
{
    public class HttpWebRequestResource : IDisposable
    {
        private static object ResourceLock = new object();
        private static HashSet<HttpWebRequest> ResourceSet = new HashSet<HttpWebRequest>(); 

        private HttpWebRequest Request;

        public static void AbortAll()
        {
            lock (ResourceLock) {
                foreach (HttpWebRequest request in ResourceSet) {
                    request.Abort ();
                }
            }
        }

        public HttpWebRequestResource()
        {
            Request = null;
        }

        public void StartResource(HttpWebRequest request)
        {
            lock (ResourceLock) {
                if (Request != null) {
                    ResourceSet.Remove (Request);
                }
                Request = request;
                ResourceSet.Add (Request);
            }
        }

        public void Dispose()
        {
            if (Request != null) {
                lock (ResourceLock) {
                    ResourceSet.Remove (Request);
                }
            }
        }
    }

}

namespace DotCMIS.Binding.Impl
{
    internal static class HttpUtils
    {
        public delegate void Output(Stream stream);

        public static Response InvokeGET(UrlBuilder url, BindingSession session)
        {
            return Invoke(url, "GET", null, null, session, null, null, null);
        }

        public static Response InvokeGET(UrlBuilder url, BindingSession session, long? offset, long? length)
        {
            return Invoke(url, "GET", null, null, session, offset, length, null);
        }

        public static Response InvokePOST(UrlBuilder url, String contentType, Output writer, BindingSession session)
        {
            return Invoke(url, "POST", contentType, writer, session, null, null, null);
        }

        public static Response InvokePUT(UrlBuilder url, String contentType, IDictionary<string, string> headers, Output writer, BindingSession session)
        {
            return Invoke(url, "PUT", contentType, writer, session, null, null, headers);
        }

        public static Response InvokeDELETE(UrlBuilder url, BindingSession session)
        {
            return Invoke(url, "DELETE", null, null, session, null, null, null);
        }

        private static Response Invoke(UrlBuilder url, String method, String contentType, Output writer, BindingSession session,
                long? offset, long? length, IDictionary<string, string> headers)
        {
            Guid tag = Guid.NewGuid();
            string request = method + " " + url;
            Stopwatch watch = new Stopwatch();
            watch.Start();
            int maxRetries = 5;
            int.TryParse(session.GetValue(SessionParameter.MaximumRequestRetries, "5") as String, out maxRetries);
            using (HttpWebRequestResource resource = new HttpWebRequestResource ())
            {
            try
            {
                // log before connect
                Trace.WriteLineIf(DotCMISDebug.DotCMISSwitch.TraceInfo, string.Format("[{0}] starting {1}", tag.ToString(), request));

                //Handles infrequent networking conditions
                int retry = 0;
                for(;;){

                    // create connection
                    HttpWebRequest conn = (HttpWebRequest)WebRequest.Create(url.Url);
                    conn.Method = method;
                    resource.StartResource(conn);

                    // device management
                    string deviceIdentifier = session.GetValue(SessionParameter.DeviceIdentifier) as String;
                    if(deviceIdentifier != null)
                    {
                        conn.Headers.Add("Device-ID", deviceIdentifier);
                    }

                    // user agent
                    string userAgent = session.GetValue(SessionParameter.UserAgent) as String;
                    conn.UserAgent = userAgent == null ? "Apache Chemistry DotCMIS" : userAgent;

                    // timeouts
                    int connectTimeout = session.GetValue(SessionParameter.ConnectTimeout, -2);
                    if (connectTimeout >= -1)
                    {
                        conn.Timeout = connectTimeout;
                    }

                    int readTimeout = session.GetValue(SessionParameter.ReadTimeout, -2);
                    if (readTimeout >= -1)
                    {
                        conn.ReadWriteTimeout = readTimeout;
                    }

                    // set content type
                    if (contentType != null)
                    {
                        conn.ContentType = contentType;
                    }

                    // set additional headers
                    if (headers != null)
                    {
                        foreach (KeyValuePair<string, string> header in headers)
                        {
                            conn.Headers.Add(header.Key, header.Value);
                        }
                    }

                    // authenticate
                    IAuthenticationProvider authProvider = session.GetAuthenticationProvider();
                    if (authProvider != null)
                    {
                        conn.PreAuthenticate = true;
                        authProvider.Authenticate(conn);
                    }

                    // range
                    if (offset != null && length != null)
                    {
                        if (offset < Int32.MaxValue && offset + length - 1 < Int32.MaxValue)
                        {
                            conn.AddRange((int)offset, (int)offset + (int)length - 1);
                        }
                        else
                        {
                            try
                            {
                                MethodInfo mi = conn.GetType().GetMethod("AddRange", new Type[] { typeof(Int64), typeof(Int64) });
                                mi.Invoke(conn, new object[] { offset, offset + length - 1 });
                            }
                            catch (Exception e)
                            {
                                throw new CmisInvalidArgumentException("Offset or length too big!", e);
                            }
                        }
                    }
                    else if (offset != null)
                    {
                        if (offset < Int32.MaxValue)
                        {
                            conn.AddRange((int)offset);
                        }
                        else
                        {
                            try
                            {
                                MethodInfo mi = conn.GetType().GetMethod("AddRange", new Type[] { typeof(Int64) });
                                mi.Invoke(conn, new object[] { offset });
                            }
                            catch (Exception e)
                            {
                                throw new CmisInvalidArgumentException("Offset too big!", e);
                            }
                        }
                    }

                    // compression
                    string compressionFlag = session.GetValue(SessionParameter.Compression) as string;
                    if (compressionFlag != null && compressionFlag.ToLower().Equals("true"))
                    {
                        conn.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    }

                    // send data
                    if (writer != null)
                    {
                        conn.SendChunked = true;
                        Stream requestStream = conn.GetRequestStream();
                        writer(requestStream);
                        requestStream.Close();
                    }
                    else
                    {
#if __MonoCS__
                        //around for MONO HTTP DELETE issue
                        //http://stackoverflow.com/questions/11785597/monotouch-iphone-call-to-httpwebrequest-getrequeststream-connects-to-server
                        if (method == "DELETE")
                        {
                            conn.ContentLength = 0;
                            Stream requestStream = conn.GetRequestStream();
                            requestStream.Close();
                        }
#endif
                    }

                    // connect
                    try
                    {
                        HttpWebResponse response = (HttpWebResponse)conn.GetResponse();

                        if (authProvider != null)
                        {
                            authProvider.HandleResponse(response);
                        }
                        watch.Stop();
                        Trace.WriteLineIf(DotCMISDebug.DotCMISSwitch.TraceInfo, string.Format("[{0}] received response after {1} ms", tag.ToString(), watch.ElapsedMilliseconds.ToString()));

                        return new Response(response);
                    }
                    catch (WebException we)
                    {
                        if (method != "GET" || !ExceptionFixabilityDecider.CanExceptionBeFixedByRetry(we) || retry == maxRetries) {
                            watch.Stop();
                            Trace.WriteLineIf(DotCMISDebug.DotCMISSwitch.TraceInfo, string.Format("[{0}] received response after {1} ms", tag.ToString(), watch.ElapsedMilliseconds.ToString()));
                            if (we.Response != null)
                            {
                                return new Response(we);
                            }
                            else
                            {
                                throw;
                            }
                        }

                        retry++;
                        watch.Stop();
                        Thread.Sleep(50);
                        watch.Start();
                        Trace.WriteLineIf(DotCMISDebug.DotCMISSwitch.TraceInfo, string.Format("[{0}] {1} retry No {2}", tag.ToString(), we.Message, retry.ToString()));
                    }
                }
            }
            catch (Exception e)
            {
                watch.Stop();
                Trace.WriteLineIf(DotCMISDebug.DotCMISSwitch.TraceInfo, string.Format("[{0}] Cannot access {1}: {2} after {3} ms", tag.ToString(), request, e.Message, watch.ElapsedMilliseconds));
                throw new CmisConnectionException("Cannot access " + url + ": " + e.Message, e);
            }
            }
        }

        internal class Response
        {
            private readonly WebResponse response;
            public WebException Exception { get; private set; }
            public HttpStatusCode? StatusCode { get; private set; }
            public string Message { get; private set; }
            public Stream Stream { get; private set; }
            public string ErrorContent { get; private set; }
            public string ContentType { get; private set; }
            public long? ContentLength { get; private set; }
            public Dictionary<string, string[]> Headers { get; private set; }

            public Response(HttpWebResponse httpResponse)
            {
                this.response = httpResponse;
                this.ExtractHeader();
                StatusCode = httpResponse.StatusCode;
                Message = httpResponse.StatusDescription;
                ContentType = httpResponse.ContentType;
                ContentLength = httpResponse.ContentLength == -1 ? null : (long?)httpResponse.ContentLength;
                string contentTransferEncoding = httpResponse.Headers["Content-Transfer-Encoding"];
                bool isBase64 = contentTransferEncoding != null && contentTransferEncoding.Equals("base64", StringComparison.CurrentCultureIgnoreCase);
                Headers = new Dictionary<string,string[]>();
                foreach (string key in httpResponse.Headers.AllKeys)
                {
                    string[] values = httpResponse.Headers.GetValues(key);
                    Headers.Add(key, values);
                }

                if (httpResponse.StatusCode == HttpStatusCode.OK ||
                    httpResponse.StatusCode == HttpStatusCode.Created ||
                    httpResponse.StatusCode == HttpStatusCode.NonAuthoritativeInformation ||
                    httpResponse.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (isBase64)
                    {
                        Stream = new BufferedStream(new CryptoStream(httpResponse.GetResponseStream(), new FromBase64Transform(), CryptoStreamMode.Read), 64 * 1024);
                    }
                    else
                    {
                        Stream = new BufferedStream(httpResponse.GetResponseStream(), 64 * 1024);
                    }
                }
                else
                {
                    try { httpResponse.Close(); }
                    catch (Exception) { }
                }
            }

            public Response(WebException exception)
            {
                this.response = exception.Response;
                this.Exception = exception;
                this.ExtractHeader();
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null)
                {
                    StatusCode = httpResponse.StatusCode;
                    Message = httpResponse.StatusDescription;
                    ContentType = httpResponse.ContentType;

                    if (ContentType != null && (ContentType.ToLower().StartsWith("text/") || ContentType.ToLower().StartsWith("application/json;")))
                    {
                        StringBuilder sb = new StringBuilder();

                        using (StreamReader sr = new StreamReader(httpResponse.GetResponseStream()))
                        {
                            string s;
                            while ((s = sr.ReadLine()) != null)
                            {
                                sb.Append(s);
                                sb.Append('\n');
                            }
                        }

                        ErrorContent = sb.ToString();
                    }
                }
                else
                {
                    StatusCode = null;
                    Message = exception.Status.ToString();
                }

                try { response.Close(); }
                catch (Exception) { }
            }

            public void CloseStream()
            {
                if (Stream != null)
                {
                    Stream.Close();
                }
            }

            private void ExtractHeader() {
                this.Headers = new Dictionary<string, string[]>();
                if (this.response != null && this.response.Headers != null) {
                    for (int i = 0; i < this.response.Headers.Count; ++i) {
                        this.Headers.Add(this.response.Headers.GetKey(i), this.response.Headers.GetValues(i));
                    }
                }
            }
        }
    }

    public static class ExceptionFixabilityDecider {
        public static bool CanExceptionBeFixedByRetry(WebException we)
        {
            if(!(we.Response is HttpWebResponse)){
                return true;
            }
            return CanExceptionStatusCodeBeFixedByRetry((we.Response as HttpWebResponse).StatusCode);
        }

        public static bool CanExceptionStatusCodeBeFixedByRetry(HttpStatusCode code)
        {
            if(code == HttpStatusCode.NotFound || code == HttpStatusCode.Forbidden) {
                return false;
            }
            return true;
        }
    }

    internal class UrlBuilder
    {
        private UriBuilder uri;

        public Uri Url
        {
            get { return uri.Uri; }
        }

        public UrlBuilder(string url)
        {
            if (url == null)
            {
                throw new ArgumentNullException("url");
            }

            uri = new UriBuilder(url);
        }

        public UrlBuilder AddPath(string path)
        {
            if (path == null || path.Length == 0)
            {
                return this;
            }
            uri.Path = uri.Path.TrimEnd('/', '\\');
            path = path.TrimStart('/', '\\');
            uri.Path = uri.Path + "/" + path;
            return this;
        }

        public UrlBuilder AddParameter(string name, object value)
        {
            if ((name == null) || (value == null))
            {
                return this;
            }

            string valueStr = Uri.EscapeDataString(UrlBuilder.NormalizeParameter(value));

            if (uri.Query != null && uri.Query.Length > 1)
            {
                uri.Query = uri.Query.Substring(1) + "&" + name + "=" + valueStr;
            }
            else
            {
                uri.Query = name + "=" + valueStr;
            }

            return this;
        }

        public static string NormalizeParameter(object value)
        {
            if (value == null)
            {
                return null;
            }
            else if (value is Enum)
            {
                return ((Enum)value).GetCmisValue();
            }
            else if (value is bool)
            {
                return (bool)value ? "true" : "false";
            }

            return value.ToString();
        }

        public override string ToString()
        {
            return Url.ToString();
        }
    }

    internal class UrlParser
    {
        private Uri uri;

        public UrlParser(string url)
        {
            if (url == null)
            {
                throw new ArgumentNullException("url");
            }

            uri = new Uri(url);
        }

        public string GetQueryValue(string name)
        {
            string query = uri.Query;
            if (!query.Contains("?"))
            {
                return null;
            }
            query = query.Substring(query.IndexOf('?') + 1);

            foreach (string segment in query.Split('&'))
            {
                string[] parts = segment.Split('=');
                if(parts[0] != name)
                {
                    continue;
                }
                if (parts.Length == 1)
                {
                    return string.Empty;
                }
                if (parts.Length == 2)
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
                return null;
            }
            return null;
        }
    }

    internal class MimeHelper
    {
        public const string ContentDisposition = "Content-Disposition";
        public const string DispositionAttachment = "attachment";
        public const string DispositionFilename = "filename";
        public const string DispositionName = "name";
        public const string DispositionFormDataContent = "form-data; " + DispositionName + "=\"content\"";

        private const string MIMESpecials = "()<>@,;:\\\"/[]?=" + "\t ";
        private const string RFC2231Specials = "*'%" + MIMESpecials;
        private static char[] HexDigits = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };
        private static byte[] HexDecode = new byte[0x80];
   
              
        public static string EncodeContentDisposition(string disposition, string filename)
        {
            if (disposition == null)
            {
                disposition = DispositionAttachment;
            }
            return disposition + EncodeRFC2231(DispositionFilename, filename);
        }

        protected static string EncodeRFC2231(string key, string value)
        {
            StringBuilder buf = new StringBuilder();
            bool encoded = EncodeRFC2231value(value, buf);
            if (encoded)
            {
                return "; " + key + "*=" + buf.ToString();
            }
            else
            {
                return "; " + key + "=" + value;
            }
        }

        protected static bool EncodeRFC2231value(string value, StringBuilder buf)
        {
            buf.Append("UTF-8");
            buf.Append("''"); // no language
            byte[] bytes;
            try
            {
                bytes = UTF8Encoding.UTF8.GetBytes(value);
            }
            catch (Exception)
            {
                return true;
            }

            bool encoded = false;
            for (int i = 0; i < bytes.Length; i++)
            {
                int ch = bytes[i] & 0xff;
                if (ch <= 32 || ch >= 127 || RFC2231Specials.IndexOf((char)ch) != -1)
                {
                    buf.Append('%');
                    buf.Append(HexDigits[ch >> 4]);
                    buf.Append(HexDigits[ch & 0xf]);
                    encoded = true;
                }
                else
                {
                    buf.Append((char)ch);
                }
            }
            return encoded;
        }

        protected static string DecodeRFC2231value(string value)
        {
            int q1 = value.IndexOf('\'');
            if (q1 == -1)
            {
                return value;
            }
            string mimeCharset = value.Substring(0, q1);
            int q2 = value.IndexOf('\'', q1 + 1);
            if (q2 == -1)
            {
                return value;
            }
            byte[] bytes = FromHex(value.Substring(q2 + 1));
            try
            {
                return UTF8Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return value;
            }
        }

        protected static byte[] FromHex(string data)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                for (int i = 0; i < HexDigits.Length; i++)
                {
                    HexDecode[(int)HexDigits[i]] = (byte)i;
                    HexDecode[(int)Char.ToLower(HexDigits[i])] = (byte)i;
                }

                for (int i = 0; i < data.Length; )
                {
                    char c = data[i++];
                    if (c == '%')
                    {
                        if (i > data.Length - 2)
                        {
                            break;
                        }
                        byte b1 = HexDecode[data[i++] & 0x7f];
                        byte b2 = HexDecode[data[i++] & 0x7f];
                        stream.WriteByte((byte)((b1 << 4) | b2));
                    }
                    else
                    {
                        stream.WriteByte((byte)c);
                    }

                }
                return stream.ToArray();
            }
        }

        public static string DecodeContentDisposition(string value, Dictionary<string, string> parameters)
        {
            try
            {
                HeaderTokenizer tokenizer = new HeaderTokenizer(value);
                Token token = tokenizer.Next();
                if (token.Type != Token.Atomic)
                {
                    return null;
                }
                string disposition = token.Value;

                string remainder = tokenizer.Remainder;
                if (remainder != null)
                {
                    GetParameters(remainder, parameters);
                }

                return disposition;
            }
            catch (ParseException)
            {
                return null;
            }
        }

        private static void GetParameters(string list, Dictionary<string, string> parameters)
        {
            HeaderTokenizer tokenizer = new HeaderTokenizer(list);
            while (true)
            {
                Token token = tokenizer.Next();
                switch (token.Type)
                {
                    case Token.EOF:
                        return;
                    case ';':
                        token = tokenizer.Next();
                        if (token.Type == Token.EOF)
                        {
                            return;
                        }
                        if (token.Type != Token.Atomic)
                        {
                            throw new ParseException("Invalid parameter name: " + token.Value);
                        }
                        string name = token.Value.ToLower(new System.Globalization.CultureInfo("en"));
                        token = tokenizer.Next();
                        if (token.Type != '=')
                        {
                            throw new ParseException("Missing '='");
                        }
                        token = tokenizer.Next();
                        if (token.Type != Token.Atomic && token.Type != Token.QuotedString)
                        {
                            throw new ParseException("Invalid parameter value: " + token.Value);
                        }
                        string value = token.Value;
                        if (name.EndsWith("*"))
                        {
                            name = name.Substring(0, name.Length - 1);
                            value = DecodeRFC2231value(value);
                        }
                        parameters[name] = value;
                        break;
                    default:
                        throw new ParseException("Missing ';'");
                }
            }
        }

        [Serializable]
        public class ParseException : FormatException
        {
            public ParseException(string message)
                : base(message)
                {
                }

            public ParseException()
            {
            }

            public ParseException(string message, Exception inner)
                : base(message, inner)
            {
            }

            protected ParseException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }

        protected class Token
        {
            public const int Atomic = -1;
            public const int QuotedString = -2;
            public const int Comment = -3;
            public const int EOF = -4;

            public int Type { get; private set; }
            public string Value { get; private set; }

            public Token(int type, string value)
            {
                Type = type;
                Value = value;
            }
        }

        protected class HeaderTokenizer
        {
            private static readonly Token EOF = new Token(Token.EOF, null);

            private readonly string Header;
            private readonly string Delimiters;
            private readonly bool SkipComments;
            private int pos;

            public HeaderTokenizer(string header)
                : this(header, MIMESpecials, true)
            {
            }

            protected HeaderTokenizer(string header, string delimiters, bool skipComments)
            {
                Header = header;
                Delimiters = delimiters;
                SkipComments = skipComments;
            }

            public string Remainder
            {
                get
                {
                    if (pos >= Header.Length)
                    {
                        return null;
                    }
                    else
                    {
                        return Header.Substring(pos);
                    }
                }
            }

            public Token Next()
            {
                return ReadToken();
            }

            private Token ReadToken()
            {
                while (pos < Header.Length && char.IsWhiteSpace(Header[pos]))
                {
                    ++pos;
                }

                if (pos >= Header.Length)
                {
                    return EOF;
                }

                char c = Header[pos];
                if (c == '(')
                {
                    Token comment = ReadComment();
                    if (SkipComments)
                    {
                        return ReadToken();
                    }
                    else
                    {
                        return comment;
                    }
                }
                else if (c == '\"')
                {
                    return ReadQuotedString();
                }
                else if (c < 32 || c >= 127 || Delimiters.Contains(new string(c, 1)))
                {
                    pos++;
                    return new Token((int)c, new string(c, 1));
                }
                else
                {
                    return ReadAtomicToken();
                }
            }

            private Token ReadAtomicToken()
            {
                int start = pos;
                while (++pos < Header.Length)
                {
                    char c = Header[pos];
                    if (Delimiters.Contains(new string(c, 1)) || c < 32 || c >= 127)
                    {
                        break;
                    }
                }
                return new Token(Token.Atomic, Header.Substring(start, pos - start));
            }

            private Token ReadQuotedString()
            {
                int start = pos + 1;
                bool requireEscape = false;
                while (++pos < Header.Length)
                {
                    char c = Header[pos];
                    if (c == '"')
                    {
                        string value;
                        if (requireEscape)
                        {
                            value = GetEscapedValue(start, pos - start);
                        }
                        else
                        {
                            value = Header.Substring(start, pos - start);
                        }
                        return new Token(Token.Comment, value);
                    }
                    else if (c == '\\')
                    {
                        pos++;
                        requireEscape = true;
                    }
                    else if (c == '\r')
                    {
                        requireEscape = true;
                    }
                }
                throw new ParseException("Missing '\"'");
            }

            private Token ReadComment()
            {
                int start = pos + 1;
                int nesting = 1;
                bool requireEscape = false;
                while (++pos < Header.Length)
                {
                    char c = Header[pos];
                    if (c == ')')
                    {
                        nesting--;
                        if (nesting == 0)
                        {
                            break;
                        }
                    }
                    else if (c == '(')
                    {
                        nesting++;
                    }
                    else if (c == '\\')
                    {
                        pos++;
                        requireEscape = true;
                    }
                    else if (c == '\r')
                    {
                        requireEscape = true;
                    }
                }
                if (nesting != 0)
                {
                    throw new ParseException("Unbalanced comments");
                }
                string value;
                if (requireEscape)
                {
                    value = GetEscapedValue(start, pos);
                }
                else
                {
                    value = Header.Substring(start, pos - start);
                }
                pos++;
                return new Token(Token.Comment, value);
            }

            private string GetEscapedValue(int start, int end)
            {
                StringBuilder value = new StringBuilder();
                for (int i = start; i < end; i++)
                {
                    char c = Header[i];
                    if (c == '\\')
                    {
                        i++;
                        if (i == end)
                        {
                            throw new ParseException("Invalid escape character");
                        }
                        value.Append(Header[i]);
                    }
                    else if (c == '\r')
                    {
                        if (i < end - 1 && Header[i + 1] == '\n')
                        {
                            i++;
                        }
                    }
                    else
                    {
                        value.Append(Header[i]);
                    }
                }
                return value.ToString();
            }
        }
    }
}

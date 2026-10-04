using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MyHomelabBrowser.classes.Import
{
    /// <summary>Identifiant lu dans un export de mots de passe.</summary>
    public sealed record ImportedCredential(string Url, string Username, string Password, string? Totp);

    /// <summary>
    /// Mots de passe exportés par un autre navigateur ou gestionnaire, au format CSV : Chrome,
    /// Edge, Brave, Opera (name,url,username,password,note), Firefox (url,username,password,…),
    /// Safari (Title,URL,Username,Password,Notes,OTPAuth), Bitwarden (login_uri,login_username,
    /// login_password,login_totp). Les colonnes sont reconnues par leur nom. Seules les
    /// adresses web gardent leurs identifiants (pas les applications Android, par exemple).
    /// </summary>
    public static class PasswordCsvImporter
    {
        static readonly string[] UrlColumns = { "url", "login_uri", "origin", "website", "web site", "uri" };
        static readonly string[] UsernameColumns = { "username", "login_username", "user", "login", "email", "user name" };
        static readonly string[] PasswordColumns = { "password", "login_password" };
        static readonly string[] TotpColumns = { "otpauth", "login_totp", "totp" };

        /// <summary>Identifiants du fichier, et nombre de lignes ignorées ; null si ce n'est pas un export reconnu.</summary>
        public static (List<ImportedCredential> Credentials, int Skipped)? Parse(string csv)
        {
            List<List<string>> rows = ReadRows(csv);
            if (rows.Count == 0)
                return null;
            List<string> header = rows[0].Select(h => h.Trim().TrimStart('﻿').ToLowerInvariant()).ToList();
            int url = Column(header, UrlColumns);
            int username = Column(header, UsernameColumns);
            int password = Column(header, PasswordColumns);
            int totp = Column(header, TotpColumns);
            if (url < 0 || password < 0)
                return null;

            var credentials = new List<ImportedCredential>();
            int skipped = 0;
            foreach (List<string> row in rows.Skip(1))
            {
                if (row.All(string.IsNullOrWhiteSpace))
                    continue;
                string address = Cell(row, url).Trim();
                string secret = Cell(row, password);
                if (secret.Length == 0 || !Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.Host.Length == 0)
                {
                    skipped++;
                    continue;
                }
                string? code = totp >= 0 && Cell(row, totp).Trim() is { Length: > 0 } value ? value : null;
                credentials.Add(new ImportedCredential(address, username >= 0 ? Cell(row, username).Trim() : string.Empty, secret, code));
            }
            return (credentials, skipped);
        }

        static int Column(List<string> header, string[] names)
        {
            foreach (string name in names)
            {
                int index = header.IndexOf(name);
                if (index >= 0)
                    return index;
            }
            return -1;
        }

        static string Cell(List<string> row, int index) => index >= 0 && index < row.Count ? row[index] : string.Empty;

        /// <summary>CSV (RFC 4180) : champs entre guillemets, guillemets doublés, retours à la ligne dans un champ.</summary>
        public static List<List<string>> ReadRows(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            bool fieldStarted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }
                switch (c)
                {
                    case '"' when !fieldStarted || field.Length == 0:
                        quoted = true;
                        fieldStarted = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        fieldStarted = false;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        fieldStarted = false;
                        rows.Add(row);
                        row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        fieldStarted = true;
                        break;
                }
            }
            if (fieldStarted || field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }
    }
}

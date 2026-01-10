using System;
using System.Collections.Generic;
using System.Text;

namespace MyHomelabBrowser.classes
{
   public class FavoriteItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Folder { get; set; } // null = racine
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

}

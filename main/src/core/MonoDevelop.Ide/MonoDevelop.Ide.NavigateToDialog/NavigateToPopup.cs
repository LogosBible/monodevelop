// 
// NavigateToPopup.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2012 Xamarin Inc. (http://xamarin.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using MonoDevelop.Components;
using System.Collections.Generic;
using System.Threading;
using MonoDevelop.Core.Instrumentation;
using Gtk;
using System.ComponentModel;
using MonoDevelop.Projects;
using ICSharpCode.NRefactory.TypeSystem;
using System.Linq;
using MonoDevelop.Ide.TypeSystem;
using MonoDevelop.Core.Text;
using Gdk;
using MonoDevelop.Ide.Gui;


namespace MonoDevelop.Ide.NavigateToDialog
{
	public class NavigateToPopup : Gtk.Window
	{
		ListView list;
		ScrolledWindow scrolledWindow = new ScrolledWindow ();
		public NavigateToType NavigateToType {
			get;
			set;
		}
		
		public struct OpenLocation
		{
			public string Filename;
			public int Line;
			public int Column;
			
			public OpenLocation (string filename, int line, int column)
			{
				this.Filename = filename;
				this.Line = line;
				this.Column = column;
			}
		}
		
		List<OpenLocation> locations = new List<OpenLocation> ();
		public IEnumerable<OpenLocation> Locations {
			get {
				return locations.ToArray ();
			}
		}

		string query;

		public string Query {
			get {
				return query;
			}
			set {
				query = value;
				PerformSearch ();
			}
		}
		
		bool useFullSearch;
		bool isAbleToSearchMembers;
		public NavigateToPopup (NavigateToType navigateTo, bool isAbleToSearchMembers) : base (Gtk.WindowType.Popup)
		{
			this.TypeHint = WindowTypeHint.Dialog;
			this.TransientFor = IdeApp.Workbench.RootWindow;
			this.DestroyWithParent = true;
			this.SkipPagerHint = true;
			this.SkipTaskbarHint = true;
			this.Decorated = false;
			this.NavigateToType = navigateTo;
			this.isAbleToSearchMembers = isAbleToSearchMembers;
			this.Add (scrolledWindow);
			SetupTreeView ();
			StartCollectThreads ();
		}
		
		Thread collectFiles;
		void StartCollectThreads ()
		{
			StartCollectFiles ();
		}
		
		static TimerCounter getMembersTimer = InstrumentationService.CreateTimerCounter ("Time to get all members", "NavigateToDialog");
		
		void StartCollectFiles ()
		{
			ThreadPool.QueueUserWorkItem (delegate {
				files = GetFiles ();
			});
		}
		
		void SetupTreeView ()
		{
			list = new ListView ();
			list.AllowMultipleSelection = true;
			list.DataSource = new ResultsDataSource (this);
			list.Show ();
			list.ItemActivated += delegate { 
				OpenFile ();
			};
			scrolledWindow.Add (list);
		}
		
		void OpenFile ()
		{
			locations.Clear ();
			if (list.SelectedRows.Count != 0) {
				foreach (int sel in list.SelectedRows) {
					var res = lastResult.results [sel];
					if (res.File == null)
						continue;
					var loc = new OpenLocation (res.File, res.Row, res.Column);
					if (loc.Line == -1) {
						int i = Query.LastIndexOf (':');
						if (i != -1) {
							if (!int.TryParse (Query.Substring (i + 1), out loc.Line))
								loc.Line = -1;
						}
					}
					locations.Add (loc);
				}
				foreach (var loc in locations)
					IdeApp.Workbench.OpenDocument (loc.Filename, loc.Line, loc.Column);
				Destroy ();
			}
		}

		protected override void OnDestroyed ()
		{
			StopActiveSearch ();
			Detach ();
			base.OnDestroyed ();
		}
		 
		System.ComponentModel.BackgroundWorker searchWorker = null;

		void StopActiveSearch ()
		{
			if (searchWorker != null) 
				searchWorker.CancelAsync ();
			searchWorker = null;
		}
		
		void PerformSearch ()
		{
			StopActiveSearch ();
			
			WaitForCollectFiles ();
			
			string toMatch = Query;
			
			if (string.IsNullOrEmpty (toMatch)) {
				list.DataSource = new ResultsDataSource (this);
//				labelResults.LabelProp = GettextCatalog.GetString ("_Results: Enter search term to start.");
				return;
			} else {
//				labelResults.LabelProp = GettextCatalog.GetString ("_Results: Searching...");
			}
			
			if (lastResult != null && !string.IsNullOrEmpty (lastResult.pattern) && toMatch.StartsWith (lastResult.pattern))
				list.DataSource = new ResultsDataSource (this);
			
			searchWorker = new System.ComponentModel.BackgroundWorker  ();
			searchWorker.WorkerSupportsCancellation = true;
			searchWorker.WorkerReportsProgress = false;
			searchWorker.DoWork += SearchWorker;
			
			searchWorker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
				if (e.Cancelled)
					return;
				Application.Invoke (delegate {
					lastResult = e.Result as WorkerResult;
					if (lastResult != null && lastResult.results != null) {
						list.DataSource = lastResult.results;
						list.SelectedRow = 0;
						list.CenterViewToSelection ();
					}
//					labelResults.LabelProp = String.Format (GettextCatalog.GetPluralString ("_Results: {0} match found.", "_Results: {0} matches found.", lastResult.results.ItemCount), lastResult.results.ItemCount);
				});
			};
			
			searchWorker.RunWorkerAsync (new KeyValuePair<string, WorkerResult> (toMatch, lastResult ?? new WorkerResult (this)));
		}
		
		class WorkerResult 
		{
			public List<ProjectFile> filteredFiles = null;
			public List<ITypeDefinition> filteredTypes = null;
			public List<IMember> filteredMembers  = null;
			
			public string pattern = null;
			public bool isGotoFilePattern;
			public ResultsDataSource results;
			
			public bool FullSearch;
			
			public bool IncludeFiles, IncludeTypes, IncludeMembers;
			
			public Ambience ambience;
			
			public StringMatcher matcher = null;
			
			public WorkerResult (Widget widget)
			{
				results = new ResultsDataSource (widget);
			}
			
			internal SearchResult CheckFile (ProjectFile file)
			{
				int rank;
				string matchString = System.IO.Path.GetFileName (file.FilePath);
				if (MatchName (matchString, out rank)) 
					return new FileSearchResult (pattern, matchString, rank, file, true);
				
				if (!FullSearch)
					return null;
				matchString = FileSearchResult.GetRelProjectPath (file);
				if (MatchName (matchString, out rank)) 
					return new FileSearchResult (pattern, matchString, rank, file, false);
				
				return null;
			}
			
			internal SearchResult CheckType (ITypeDefinition type)
			{
				int rank;
				if (MatchName (type.Name, out rank))
					return new TypeSearchResult (pattern, type.Name, rank, type, false) { Ambience = ambience };
				if (!FullSearch)
					return null;
				if (MatchName (type.FullName, out rank))
					return new TypeSearchResult (pattern, type.FullName, rank, type, true) { Ambience = ambience };
				return null;
			}
			
			internal SearchResult CheckMember (IMember member)
			{
				int rank;
				bool useDeclaringTypeName = member is IMethod && (((IMethod)member).IsConstructor || ((IMethod)member).IsDestructor);
				string memberName = useDeclaringTypeName ? member.DeclaringType.Name : member.Name;
				if (MatchName (memberName, out rank))
					return new MemberSearchResult (pattern, memberName, rank, member, false) { Ambience = ambience };
				if (!FullSearch)
					return null;
				memberName = useDeclaringTypeName ? member.DeclaringType.FullName : member.FullName;
				if (MatchName (memberName, out rank))
					return new MemberSearchResult (pattern, memberName, rank, member, true) { Ambience = ambience };
				return null;
			}
			
			Dictionary<string, MatchResult> savedMatches = new Dictionary<string, MatchResult> ();
			bool MatchName (string name, out int matchRank)
			{
				MatchResult savedMatch;
				if (!savedMatches.TryGetValue (name, out savedMatch)) {
					bool doesMatch = matcher.CalcMatchRank (name, out matchRank);
					savedMatches[name] = savedMatch = new MatchResult (doesMatch, matchRank);
				}
				
				matchRank = savedMatch.Rank;
				return savedMatch.Match;
			}
		}
		
		IEnumerable<ProjectFile> files;
		
		IEnumerable<IMember> members {
			get {
				getMembersTimer.BeginTiming ();
				try {
					lock (members) {
						foreach (var type in types) {
							foreach (var m in type.Members) {
								yield return m;
							}
						}
					}
				} finally {
					getMembersTimer.EndTiming ();
				}
				
			}
		}
		
		WorkerResult lastResult;
		
		void SearchWorker (object sender, DoWorkEventArgs e)
		{
			BackgroundWorker worker = (BackgroundWorker)sender;
			var arg = (KeyValuePair<string, WorkerResult>)e.Argument;
			
			WorkerResult lastResult = arg.Value;
			
			WorkerResult newResult = new WorkerResult (this);
			newResult.pattern = arg.Key;
			newResult.IncludeFiles = (NavigateToType & NavigateToType.Files) == NavigateToType.Files;
			newResult.IncludeTypes = (NavigateToType & NavigateToType.Types) == NavigateToType.Types;
			newResult.IncludeMembers = (NavigateToType & NavigateToType.Members) == NavigateToType.Members;
			var firstType = types.FirstOrDefault ();
			newResult.ambience = firstType != null ? AmbienceService.GetAmbienceForFile (firstType.Region.FileName) : AmbienceService.DefaultAmbience;
			
			string toMatch = arg.Key;
			int i = toMatch.IndexOf (':');
			if (i != -1) {
				toMatch = toMatch.Substring (0,i);
				newResult.isGotoFilePattern = true;
			}
			newResult.matcher = StringMatcher.GetMatcher (toMatch, true);
			newResult.FullSearch = useFullSearch;
			
			foreach (SearchResult result in AllResults (worker, lastResult, newResult)) {
				if (worker.CancellationPending)
					break;
				newResult.results.AddResult (result);
			}
			
			if (worker.CancellationPending) {
				e.Cancel = true;
				return;
			}
			newResult.results.Sort (new DataItemComparer ());
			
			e.Result = newResult;
		}
		
		IEnumerable<SearchResult> AllResults (BackgroundWorker worker, WorkerResult lastResult, WorkerResult newResult)
		{
			// Search files
			if (newResult.IncludeFiles) {
				newResult.filteredFiles = new List<ProjectFile> ();
				bool startsWithLastFilter = lastResult != null && lastResult.pattern != null && newResult.pattern.StartsWith (lastResult.pattern) && lastResult.filteredFiles != null;
				IEnumerable<ProjectFile> allFiles = startsWithLastFilter ? lastResult.filteredFiles : files;
				foreach (ProjectFile file in allFiles) {
					if (worker.CancellationPending) 
						yield break;
					SearchResult curResult = newResult.CheckFile (file);
					if (curResult != null) {
						newResult.filteredFiles.Add (file);
						yield return curResult;
					}
				}
			}
			if (newResult.isGotoFilePattern)
				yield break;
			
			// Search Types
			if (newResult.IncludeTypes) {
				newResult.filteredTypes = new List<ITypeDefinition> ();
				lock (types) {
					bool startsWithLastFilter = lastResult.pattern != null && newResult.pattern.StartsWith (lastResult.pattern) && lastResult.filteredTypes != null;
					var allTypes = startsWithLastFilter ? lastResult.filteredTypes : types;
					foreach (var type in allTypes) {
						if (worker.CancellationPending)
							yield break;
						SearchResult curResult = newResult.CheckType (type);
						if (curResult != null) {
							newResult.filteredTypes.Add (type);
							yield return curResult;
						}
					}
				}
			}
			
			// Search members
			if (newResult.IncludeMembers) {
				newResult.filteredMembers = new List<IMember> ();
				lock (members) {
					bool startsWithLastFilter = lastResult.pattern != null && newResult.pattern.StartsWith (lastResult.pattern) && lastResult.filteredMembers != null;
					var allMembers = startsWithLastFilter ? lastResult.filteredMembers : members;
					foreach (var member in allMembers) {
						if (worker.CancellationPending)
							yield break;
						SearchResult curResult = newResult.CheckMember (member);
						if (curResult != null) {
							newResult.filteredMembers.Add (member);
							yield return curResult;
						}
					}
				}
			}
		}
		
		
		void WaitForCollectFiles ()
		{
			if (collectFiles != null) {
				collectFiles.Join ();
				collectFiles = null;
			}
		}
		
		class DataItemComparer : IComparer<SearchResult>
		{
			public int Compare (SearchResult o1, SearchResult o2)
			{
				var r = o2.Rank.CompareTo (o1.Rank);
				if (r == 0)
					r = o1.SearchResultType.CompareTo (o2.SearchResultType);
				if (r == 0)
					return String.CompareOrdinal (o1.MatchedString, o2.MatchedString);
				return r;
			}
		}
		
		IEnumerable<ProjectFile> GetFiles ()
		{
			HashSet<ProjectFile> list = new HashSet<ProjectFile> ();
			foreach (Document doc in IdeApp.Workbench.Documents) {
				// We only want to check it here if it's not part
				// of the open combine.  Otherwise, it will get
				// checked down below.
				if (doc.Project == null && doc.IsFile)
					list.Add (new ProjectFile (doc.Name));
			}
			
			var projects = IdeApp.Workspace.GetAllProjects ();

			foreach (Project p in projects) {
				foreach (ProjectFile file in p.Files) {
					if (file.Subtype != Subtype.Directory)
						list.Add (file);
				}
			}
			return list;
		}
		
		static TimerCounter getTypesTimer = InstrumentationService.CreateTimerCounter ("Time to get all types", "NavigateToDialog");
		
		
		IEnumerable<ITypeDefinition> types {
			get {
				getTypesTimer.BeginTiming ();
				try {
					foreach (Document doc in IdeApp.Workbench.Documents) {
						// We only want to check it here if it's not part
						// of the open combine. Otherwise, it will get
						// checked down below.
						if (doc.Project == null && doc.IsFile) {
							var info = doc.ParsedDocument;
							if (info != null) {
								var ctx = doc.Compilation;
								foreach (var type in ctx.MainAssembly.GetAllTypeDefinitions ()) {
									yield return type;
								}
							}
						}
					}
					
					var projects = IdeApp.Workspace.GetAllProjects ();
					
					foreach (Project p in projects) {
						var pctx = TypeSystemService.GetCompilation (p);
						foreach (var type in pctx.MainAssembly.GetAllTypeDefinitions ())
							yield return type;
					}
				} finally {
					getTypesTimer.EndTiming ();
				}
			}
		}

		struct MatchResult 
		{
			public bool Match;
			public int Rank;
			
			public MatchResult (bool match, int rank)
			{
				this.Match = match;
				this.Rank = rank;
			}
		}
		
		protected virtual void HandleKeyPress (object o, KeyPressEventArgs args)
		{
			// Up and down move the tree selection up and down
			// for rapid selection changes.
			Gdk.EventKey key = args.Event;
			switch (key.Key) {
			case Gdk.Key.Page_Down:
				list.ModifySelection (false, true, (args.Event.State & ModifierType.ShiftMask) == ModifierType.ShiftMask);
				args.RetVal = true;
				break;
			case Gdk.Key.Page_Up:
				list.ModifySelection (true, true, (args.Event.State & ModifierType.ShiftMask) == ModifierType.ShiftMask);
				args.RetVal = true;
				break;
			case Gdk.Key.Up:
				list.ModifySelection (true, false, (args.Event.State & ModifierType.ShiftMask) == ModifierType.ShiftMask);
				args.RetVal = true;
				break;
			case Gdk.Key.Down:
				list.ModifySelection (false, false, (args.Event.State & ModifierType.ShiftMask) == ModifierType.ShiftMask);
				args.RetVal = true;
				break;
			case Gdk.Key.Escape:
				Destroy ();
				args.RetVal = true;
				break;
			}
		}

		SearchEntry matchEntry;
		public void Attach (SearchEntry matchEntry)
		{
			this.matchEntry = matchEntry;
			matchEntry.Entry.KeyPressEvent += HandleKeyPress;
			matchEntry.Activated += HandleActivated;
		}


		void Detach ()
		{
			if (matchEntry != null) {
				matchEntry.Entry.KeyPressEvent -= HandleKeyPress;
				matchEntry.Activated -= HandleActivated;
				matchEntry = null;
			}
		}

		void HandleActivated (object sender, EventArgs e)
		{
			OpenFile ();
		}

	}
}

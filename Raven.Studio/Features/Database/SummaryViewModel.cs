﻿using Raven.Abstractions.Commands;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Client.Connection.Async;
using Raven.Json.Linq;
using Raven.Studio.Infrastructure.Navigation;

namespace Raven.Studio.Features.Database
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel.Composition;
	using System.IO;
	using System.Linq;
	using System.Threading.Tasks;
	using Abstractions.Data;
	using Caliburn.Micro;
	using Client;
	using Collections;
	using Documents;
	using Framework;
	using Framework.Extensions;
	using Messages;
	using Newtonsoft.Json;
	using Plugins;
	using Plugins.Database;

	[Export]
	[ExportDatabaseExplorerItem(DisplayName = "Summary", Index = 10)]
	public class SummaryViewModel : RavenScreen,
									IHandle<DocumentDeleted>,
									IHandle<StatisticsUpdated>
	{
		[ImportingConstructor]
		public SummaryViewModel()
		{
			Events.Subscribe(this);

			DisplayName = "Summary";

			Server.CurrentDatabaseChanged += delegate
			{
				Collections = new BindableCollection<NameAndCount>();
				RecentDocuments = new BindableCollection<DocumentViewModel>();
				RetrieveSummary();

				CollectionsStatus = "Retrieving collections.";
				RecentDocumentsStatus = "Retrieving recent documents.";
				ShowCreateSampleData = false;
				IsGeneratingSampleData = false;

				NotifyOfPropertyChange(string.Empty);
			};
		}

		public string DatabaseName { get { return Server.CurrentDatabase; } }

		public BindableCollection<DocumentViewModel> RecentDocuments { get; private set; }

		public BindableCollection<NameAndCount> Collections { get; private set; }

		string collectionsStatus;
		public string CollectionsStatus
		{
			get { return collectionsStatus; }
			set
			{
				collectionsStatus = value;
				NotifyOfPropertyChange(() => CollectionsStatus);
			}
		}

		string recentDocumentsStatus;
		public string RecentDocumentsStatus
		{
			get { return recentDocumentsStatus; }
			set
			{
				recentDocumentsStatus = value;
				NotifyOfPropertyChange(() => RecentDocumentsStatus);
			}
		}

		public long LargestCollectionCount
		{
			get
			{
				return (Collections == null || !Collections.Any())
						? 0
						: Collections.Max(x => x.Count);
			}
		}

		bool showCreateSampleData;

		public bool ShowCreateSampleData
		{
			get { return showCreateSampleData; }
			set { showCreateSampleData = value; NotifyOfPropertyChange(() => ShowCreateSampleData); }
		}

		public void BeginCreateSampleData()
		{
			CreateSampleData()
				.ExecuteInSequence(success =>
				{
					if(success)
						WorkCompleted("created sample data");
				});
		}

		IEnumerable<Task> CreateSampleData()
		{
			// this code assumes a small enough dataset, and doesn't do any sort
			// of paging or batching whatsoever.

			ShowCreateSampleData = false;
			IsGeneratingSampleData = true;

			WorkStarted("creating sample data");
			WorkStarted("creating sample indexes");
			using (var documentSession = Server.OpenSession())
			using (var sampleData = typeof(SummaryViewModel).Assembly.GetManifestResourceStream("Raven.Studio.SampleData.MvcMusicStore_Dump.json"))
			using (var streamReader = new StreamReader(sampleData))
			{
				var musicStoreData = (RavenJObject)RavenJToken.ReadFrom(new JsonTextReader(streamReader));
				foreach (var index in musicStoreData.Value<RavenJArray>("Indexes"))
				{
					var indexName = index.Value<string>("name");
					var ravenJObject = index.Value<RavenJObject>("definition");
					var putDoc = documentSession.Advanced.AsyncDatabaseCommands
						.PutIndexAsync(indexName,
										ravenJObject.JsonDeserialization<IndexDefinition>(),
										true);
					yield return putDoc;
				}

				WorkCompleted("creating sample indexes");

				var batch = documentSession.Advanced.AsyncDatabaseCommands
					.BatchAsync(
						musicStoreData.Value<RavenJArray>("Docs").OfType<RavenJObject>().Select(
						doc =>
						{
							var metadata = doc.Value<RavenJObject>("@metadata");
							doc.Remove("@metadata");
							return new PutCommandData
										{
											Document = doc,
											Metadata = metadata,
											Key = metadata.Value<string>("@id"),
										};
						}).ToArray()
					);
				yield return batch;

				WorkCompleted("creating sample data");
				IsGeneratingSampleData = false;
				RecentDocumentsStatus = "Retrieving sample documents.";
				RetrieveSummary();
			}
		}

		bool isGeneratingSampleData;
		public bool IsGeneratingSampleData
		{
			get { return isGeneratingSampleData; }
			set { isGeneratingSampleData = value; NotifyOfPropertyChange(() => IsGeneratingSampleData); }
		}

		public void NavigateToCollection(NameAndCount collection)
		{
			Events.Publish(new DatabaseScreenRequested(() =>
														{
															var vm = IoC.Get<CollectionsViewModel>();
															vm.ActiveCollection = new CollectionViewModel { Name = collection.Name, Count = collection.Count };
															return vm;
														}));
		}

		protected override void OnActivate()
		{
			RetrieveSummary();
		}

		void RetrieveSummary()
		{
			using (var session = Server.OpenSession())
			{
				ExecuteCollectionQueryWithRetry(session, 5);

				WorkStarted("fetching recent documents");
				session.Advanced.AsyncDatabaseCommands
					.GetDocumentsAsync(0, 12)
					.ContinueWith(
						x =>
						{
							WorkCompleted("fetching recent documents");
							RecentDocuments = new BindableCollection<DocumentViewModel>(x.Result.Select(jdoc => new DocumentViewModel(jdoc)));
							NotifyOfPropertyChange(() => RecentDocuments);

							ShowCreateSampleData = RecentDocuments.Count == 0 || 
								RecentDocuments.Count == 1 && RecentDocuments[0].Id == "Raven/Users/Admin";

							RecentDocumentsStatus = RecentDocuments.Any() ? string.Empty : "The database contains no documents.";

						},
						faulted =>
						{
							WorkCompleted("fetching recent documents");
							NotifyError("Unable to retrieve recent documents from server.");
						});
			}
		}

		void ExecuteCollectionQueryWithRetry(IAsyncDocumentSession session, int retry)
		{
			WorkStarted("fetching collections");
			session.Advanced.AsyncDatabaseCommands
				.GetTermsCount("Raven/DocumentsByEntityName", "Tag", "", 128)
				.ContinueWith(task =>
					{
						if (task.Exception != null && retry > 0)
						{
							WorkCompleted("fetching collections");
							TaskEx.Delay(50)
								.ContinueWith(_ => ExecuteCollectionQueryWithRetry(session, retry - 1));
							return;
						}

						task.ContinueWith(
							x =>
							{
								WorkCompleted("fetching collections");
								Collections = new BindableCollection<NameAndCount>(x.Result);
								NotifyOfPropertyChange(() => LargestCollectionCount);
								NotifyOfPropertyChange(() => Collections);
								CollectionsStatus = Collections.Any() ? string.Empty : "The database contains no collections.";
							},
							faulted =>
							{
								WorkCompleted("fetching collections");
								const string error = "Unable to retrieve collections from server.";
								NotifyError(error);
								CollectionsStatus = error;
								NotifyOfPropertyChange(() => LargestCollectionCount);
								NotifyOfPropertyChange(() => Collections);

							});
					});
		}

		void IHandle<StatisticsUpdated>.Handle(StatisticsUpdated message)
		{
			if (!message.HasDocumentCountChanged) return;

			RetrieveSummary();
		}

		void IHandle<DocumentDeleted>.Handle(DocumentDeleted message)
		{
			RecentDocuments
				.Where(x => x.Id == message.DocumentId)
				.ToList()
				.Apply(x => RecentDocuments.Remove(x));

			//TODO: update collections
			//Collections
			//    .Where(x => x.Name == message.Document.CollectionType)
			//    .Apply(x => x.Count--);
		}
	}
}
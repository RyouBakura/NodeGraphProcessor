﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;
using System.Linq;
using System;

using Status = UnityEngine.UIElements.DropdownMenuAction.Status;

using Object = UnityEngine.Object;

namespace GraphProcessor
{
	public class BaseGraphView : GraphView
	{
		public delegate void ComputeOrderUpdatedDelegate();

		public BaseGraph							graph;

		public EdgeConnectorListener				connectorListener;

		public List< BaseNodeView >					nodeViews = new List< BaseNodeView >();
		public Dictionary< BaseNode, BaseNodeView >	nodeViewsPerNode = new Dictionary< BaseNode, BaseNodeView >();
		public List< EdgeView >						edgeViews = new List< EdgeView >();
        public List< CommentBlockView >         	commentBlockViews = new List< CommentBlockView >();

		Dictionary< Type, PinnedElementView >		pinnedElements = new Dictionary< Type, PinnedElementView >();

		CreateNodeMenuWindow						createNodeMenu;

		public event Action							initialized;
		public event ComputeOrderUpdatedDelegate	computeOrderUpdated;

		// Safe event relay from BaseGraph (safe because you are sure to always point on a valid BaseGraph
		// when one of these events is called), a graph switch can occur between two call tho
		public event Action				onExposedParameterListChanged;
		public event Action< string >	onExposedParameterModified;

		public BaseGraphView(EditorWindow window)
		{
			serializeGraphElements = SerializeGraphElementsCallback;
			canPasteSerializedData = CanPasteSerializedDataCallback;
			unserializeAndPaste = UnserializeAndPasteCallback;
            graphViewChanged = GraphViewChangedCallback;
			viewTransformChanged = ViewTransformChangedCallback;
            elementResized = ElementResizedCallback;

			InitializeManipulators();

			RegisterCallback< KeyDownEvent >(KeyDownCallback);
			RegisterCallback< DragPerformEvent >(DragPerformedCallback);
			RegisterCallback< DragUpdatedEvent >(DragUpdatedCallback);

			SetupZoom(0.05f, 2f);

			Undo.undoRedoPerformed += ReloadView;

			createNodeMenu = ScriptableObject.CreateInstance< CreateNodeMenuWindow >();
			createNodeMenu.Initialize(this, window);

			this.StretchToParentSize();
		}

		~BaseGraphView()
		{
			Undo.undoRedoPerformed -= ReloadView;
		}

		#region Callbacks

		protected override bool canCopySelection
		{
            get { return selection.Any(e => e is BaseNodeView || e is CommentBlockView); }
		}

		protected override bool canCutSelection
		{
            get { return selection.Any(e => e is BaseNodeView || e is CommentBlockView); }
		}

		string SerializeGraphElementsCallback(IEnumerable<GraphElement> elements)
		{
			var data = new CopyPasteHelper();

			foreach (var nodeView in elements.Where(e => e is BaseNodeView))
			{
				var node = ((nodeView) as BaseNodeView).nodeTarget;
				data.copiedNodes.Add(JsonSerializer.SerializeNode(node));
			}

			foreach (var commentBlockView in elements.Where(e => e is CommentBlockView))
			{
				var commentBlock = (commentBlockView as CommentBlockView).commentBlock;
				data.copiedCommentBlocks.Add(JsonSerializer.Serialize(commentBlock));
			}


			ClearSelection();

			return JsonUtility.ToJson(data, true);
		}

		bool CanPasteSerializedDataCallback(string serializedData)
		{
			try {
				return JsonUtility.FromJson(serializedData, typeof(CopyPasteHelper)) != null;
			} catch {
				return false;
			}
		}

		void UnserializeAndPasteCallback(string operationName, string serializedData)
		{
			var data = JsonUtility.FromJson< CopyPasteHelper >(serializedData);

            RegisterCompleteObjectUndo(operationName);

			foreach (var serializedNode in data.copiedNodes)
			{
				var node = JsonSerializer.DeserializeNode(serializedNode);

				if (node == null)
					continue ;

				//Call OnNodeCreated on the new fresh copied node
				node.OnNodeCreated();
				//And move a bit the new node
				node.position.position += new Vector2(20, 20);

				AddNode(node);

				//Select the new node
				AddToSelection(nodeViewsPerNode[node]);
			}

            foreach (var serializedCommentBlock in data.copiedCommentBlocks)
            {
                var commentBlock = JsonSerializer.Deserialize<CommentBlock>(serializedCommentBlock);

                //Same than for node
                commentBlock.OnCreated();
                commentBlock.position.position += new Vector2(20, 20);

                AddCommentBlock(commentBlock);
            }
		}

		GraphViewChange GraphViewChangedCallback(GraphViewChange changes)
		{
			if (changes.elementsToRemove != null)
			{
				RegisterCompleteObjectUndo("Remove Graph Elements");

				//Handle ourselves the edge and node remove
				changes.elementsToRemove.RemoveAll(e => {
					var edge = e as EdgeView;
					var node = e as BaseNodeView;
                    var commentBlock = e as CommentBlockView;
					var blackboardField = e as ExposedParameterFieldView;

					if (edge != null)
					{
						Disconnect(edge);
						return true;
					}
					else if (node != null)
					{
						node.OnRemoved();
						graph.RemoveNode(node.nodeTarget);
						RemoveElement(node);
						return true;
					}
                    else if (commentBlock != null)
                    {
                        graph.RemoveCommentBlock(commentBlock.commentBlock);
                        RemoveElement(commentBlock);
                        return true;
                    }
					else if (blackboardField != null)
					{
						graph.RemoveExposedParameter(blackboardField.parameter);
					}
					return false;
				});
			}

			return changes;
		}
		
		void GraphChangesCallback(GraphChanges changes)
		{
			if (changes.removedEdge != null)
			{
				var edge = edgeViews.FirstOrDefault(e => e.serializedEdge == changes.removedEdge);

				DisconnectView(edge);
			}
		}

		void ViewTransformChangedCallback(GraphView view)
		{
			if (graph != null)
			{
				graph.position = viewTransform.position;
				graph.scale = viewTransform.scale;
			}
		}

        void ElementResizedCallback(VisualElement elem)
        {
            var commentBlockView = elem as CommentBlockView;

            if (commentBlockView != null)
                commentBlockView.commentBlock.size = commentBlockView.GetPosition().size;
        }

		public override List< Port > GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
		{
			var compatiblePorts = new List< Port >();

			compatiblePorts.AddRange(ports.ToList().Where(p => {
				var portView = p as PortView;

				if (p.direction == startPort.direction)
					return false;

				//Check for type assignability
				if (!BaseGraph.TypesAreConnectable(startPort.portType, p.portType))
					return false;

				//Check if the edge already exists
				if (portView.GetEdges().Any(e => e.input == startPort || e.output == startPort))
					return false;

				return true;
			}));

			return compatiblePorts;
		}
		
		/// <summary>
		/// Build the contextual menu shown when right clicking inside the graph view
		/// </summary>
		/// <param name="evt"></param>
		public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
		{
			BuildCommentBlockContextualMenu(evt);
			BuildViewContextualMenu(evt);
			base.BuildContextualMenu(evt);
			BuildSelectAssetContextualMenu(evt);
			BuildSaveAssetContextualMenu(evt);
			BuildHelpContextualMenu(evt);
		}

		protected void BuildCommentBlockContextualMenu(ContextualMenuPopulateEvent evt)
		{
			Vector2 position = evt.mousePosition - (Vector2)viewTransform.position;
            evt.menu.AppendAction("Comment Block", (e) => AddSelectionsToCommentBlock(AddCommentBlock(new CommentBlock("New Comment Block", position))), DropdownMenuAction.AlwaysEnabled);
		}

		protected void BuildViewContextualMenu(ContextualMenuPopulateEvent evt)
		{
			evt.menu.AppendAction("View/Processor", (e) => ToggleView< ProcessorView >(), (e) => GetPinnedElementStatus< ProcessorView >());
		}

		protected void BuildSelectAssetContextualMenu(ContextualMenuPopulateEvent evt)
		{
			evt.menu.AppendAction("Select Asset", (e) => EditorGUIUtility.PingObject(graph), DropdownMenuAction.AlwaysEnabled);
		}

		protected void BuildSaveAssetContextualMenu(ContextualMenuPopulateEvent evt)
		{
			evt.menu.AppendAction("Save Asset", (e) => {
				EditorUtility.SetDirty(graph);
				AssetDatabase.SaveAssets();
			}, DropdownMenuAction.AlwaysEnabled);
		}

		protected void BuildHelpContextualMenu(ContextualMenuPopulateEvent evt)
		{
			evt.menu.AppendAction("Help/Reset Pinned Windows", e => {
				foreach (var kp in pinnedElements)
					kp.Value.ResetPosition();
			});
		}

		void KeyDownCallback(KeyDownEvent e)
		{
			if (e.keyCode == KeyCode.S && e.commandKey)
			{
				SaveGraphToDisk();
				e.StopPropagation();
			}
		}

		void DragPerformedCallback(DragPerformEvent e)
		{
			var mousePos = (e.currentTarget as VisualElement).ChangeCoordinatesTo(contentViewContainer, e.localMousePosition);
			var dragData = DragAndDrop.GetGenericData("DragSelection") as List< ISelectable >;

			if (dragData == null)
				return;

			var exposedParameterFieldViews = dragData.OfType<ExposedParameterFieldView>();
			if (exposedParameterFieldViews.Any())
			{
				foreach (var paramFieldView in exposedParameterFieldViews)
				{
					var paramNode = BaseNode.CreateFromType< ParameterNode >(mousePos);
					paramNode.parameterGUID = paramFieldView.parameter.guid;
					AddNode(paramNode);
				}
			}
		}

		void DragUpdatedCallback(DragUpdatedEvent e)
        {
            var dragData = DragAndDrop.GetGenericData("DragSelection") as List<ISelectable>;
            bool dragging = false;

            if (dragData != null)
            {
                // Handle drag from exposed parameter view
                if (dragData.OfType<ExposedParameterFieldView>().Any())
				{
                    dragging = true;
				}
            }

            if (dragging)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
            }
        }

		#endregion

		#region Initialization

		void ReloadView()
		{
			// Force the graph to reload his datas (Undo have updated the serialized properties of the graph
			// so the one that are not serialized need to be synchronized)
			graph.Deserialize();

			// Remove everything
			RemoveNodeViews();
			RemoveEdges();
			RemoveCommentBlocks();

			// And re-add with new up to date datas
			InitializeNodeViews();
			InitializeEdgeViews();
            InitializeCommentBlocks();

			Reload();

			UpdateComputeOrder();
		}

		public void Initialize(BaseGraph graph)
		{
			if (this.graph != null)
				SaveGraphToDisk();

			this.graph = graph;

            connectorListener = new EdgeConnectorListener(this);

			InitializeGraphView();
			InitializeNodeViews();
			InitializeEdgeViews();
			InitializeViews();
            InitializeCommentBlocks();

			UpdateComputeOrder();

			initialized?.Invoke();

			InitializeView();
		}

		void InitializeGraphView()
		{
			graph.onExposedParameterListChanged += () => onExposedParameterListChanged?.Invoke();
			graph.onExposedParameterModified += (s) => onExposedParameterModified?.Invoke(s);
			graph.onGraphChanges += GraphChangesCallback;
			viewTransform.position = graph.position;
			viewTransform.scale = graph.scale;
			nodeCreationRequest = (c) => SearchWindow.Open(new SearchWindowContext(c.screenMousePosition), createNodeMenu);
		}

		void InitializeNodeViews()
		{
			graph.nodes.RemoveAll(n => n == null);

			foreach (var node in graph.nodes)
			{
				var v = AddNodeView(node);
			}
		}

		void InitializeEdgeViews()
		{
			foreach (var serializedEdge in graph.edges)
			{
				var inputNodeView = nodeViewsPerNode[serializedEdge.inputNode];
				var outputNodeView = nodeViewsPerNode[serializedEdge.outputNode];
				var edgeView = new EdgeView() {
					userData = serializedEdge,
					input = inputNodeView.GetPortViewFromFieldName(serializedEdge.inputFieldName, serializedEdge.inputPortIdentifier),
					output = outputNodeView.GetPortViewFromFieldName(serializedEdge.outputFieldName, serializedEdge.outputPortIdentifier)
				};

				ConnectView(edgeView);
			}
		}

		void InitializeViews()
		{
			foreach (var pinnedElement in graph.pinnedElements)
			{
				if (pinnedElement.opened)
					OpenPinned(pinnedElement.editorType.type);
			}
		}

        void InitializeCommentBlocks()
        {
            foreach (var commentBlock in graph.commentBlocks)
                AddCommentBlockView(commentBlock);
        }

		protected virtual void InitializeManipulators()
		{
			this.AddManipulator(new ContentDragger());
			this.AddManipulator(new SelectionDragger());
			this.AddManipulator(new RectangleSelector());
			this.AddManipulator(new ClickSelector());
		}

		protected virtual void Reload() {}

		#endregion

		#region Graph content modification

		public bool AddNode(BaseNode node)
		{
			// This will initialize the node using the graph instance
			graph.AddNode(node);

			var view = AddNodeView(node);

			UpdateComputeOrder();

			// Call create after the node have been initialized
			view.OnCreated();

			return true;
		}

		protected BaseNodeView AddNodeView(BaseNode node)
		{
			var viewType = NodeProvider.GetNodeViewTypeFromType(node.GetType());

			if (viewType == null)
				viewType = typeof(BaseNodeView);

			var baseNodeView = Activator.CreateInstance(viewType) as BaseNodeView;
			baseNodeView.Initialize(this, node);
			AddElement(baseNodeView);

			nodeViews.Add(baseNodeView);
			nodeViewsPerNode[node] = baseNodeView;

			return baseNodeView;
		}

		protected void RemoveNodeView(BaseNodeView nodeView)
		{
			RemoveElement(nodeView);
			nodeViews.Remove(nodeView);
			nodeViewsPerNode.Remove(nodeView.nodeTarget);
		}

		void RemoveNodeViews()
		{
			foreach (var nodeView in nodeViews)
				RemoveElement(nodeView);
			nodeViews.Clear();
			nodeViewsPerNode.Clear();
		}

        public CommentBlockView AddCommentBlock(CommentBlock block)
        {
            graph.AddCommentBlock(block);
            block.OnCreated();
            return AddCommentBlockView(block);
        }

		public CommentBlockView AddCommentBlockView(CommentBlock block)
		{
			var c = new CommentBlockView();

			c.Initialize(this, block);

			AddElement(c);

            commentBlockViews.Add(c);
            return c;
		}

        public void AddSelectionsToCommentBlock(CommentBlockView view)
        {
            foreach (var selectedNode in selection)
            {
                if (selectedNode is BaseNodeView)
                {
                    if (commentBlockViews.Exists(x => x.ContainsElement(selectedNode as BaseNodeView)))
                        continue;

                    view.AddElement(selectedNode as BaseNodeView);
                }
            }
        }

		public void RemoveCommentBlocks()
		{
			foreach (var commentBlockView in commentBlockViews)
				RemoveElement(commentBlockView);
			commentBlockViews.Clear();
		}

		public bool ConnectView(EdgeView e, bool autoDisconnectInputs = true)
		{
			if (e.input == null || e.output == null)
				return false;
				
			var inputPortView = e.input as PortView;
			var outputPortView = e.output as PortView;
			var inputNodeView = inputPortView.node as BaseNodeView;
			var outputNodeView = outputPortView.node as BaseNodeView;
			
			if (inputNodeView == null || outputNodeView == null)
			{
				Debug.LogError("Connect aborted !");
				return false;
			}
				
			//If the input port does not support multi-connection, we remove them
			if (autoDisconnectInputs && !(e.input as PortView).portData.acceptMultipleEdges)
			{
				foreach (var edge in edgeViews.Where(ev => ev.input == e.input).ToList())
				{
					// TODO: do not disconnect them if the connected port is the same than the old connected
					DisconnectView(edge);
				}
			}
			// same for the output port:
			if (autoDisconnectInputs && !(e.output as PortView).portData.acceptMultipleEdges)
			{
				foreach (var edge in edgeViews.Where(ev => ev.output == e.output).ToList())
				{
					// TODO: do not disconnect them if the connected port is the same than the old connected
					DisconnectView(edge);
				}
			}

			AddElement(e);

			e.input.Connect(e);
			e.output.Connect(e);

			// If the input port have been removed by the custom port behavior
			// we try to find if it's still here
			if (e.input == null)
				e.input = inputNodeView.GetPortViewFromFieldName(inputPortView.fieldName, inputPortView.portData.identifier);
			if (e.output == null)
				e.output = inputNodeView.GetPortViewFromFieldName(outputPortView.fieldName, outputPortView.portData.identifier);

			edgeViews.Add(e);

			e.isConnected = true;

			return true;
		}

		public bool Connect(EdgeView e, bool autoDisconnectInputs = true)
		{
			if (!ConnectView(e, autoDisconnectInputs))
				return false;

			var inputPortView = e.input as PortView;
			var outputPortView = e.output as PortView;
			var inputNodeView = inputPortView.node as BaseNodeView;
			var outputNodeView = outputPortView.node as BaseNodeView;
			var inputPort = inputNodeView.nodeTarget.GetPort(inputPortView.fieldName, inputPortView.portData.identifier);
			var outputPort = outputNodeView.nodeTarget.GetPort(outputPortView.fieldName, outputPortView.portData.identifier);

			e.userData = graph.Connect(inputPort, outputPort, autoDisconnectInputs);

			UpdateComputeOrder();

			inputNodeView.nodeTarget.UpdateAllPorts();
			outputNodeView.nodeTarget.UpdateAllPorts();
			inputNodeView.RefreshPorts();
			outputNodeView.RefreshPorts();

			return true;
		}

		public void DisconnectView(EdgeView e, bool refreshPorts = true)
		{
			if (e == null)
				return ;

			RemoveElement(e);

			if (e?.input?.node is BaseNodeView inputNodeView)
			{
				e.input.Disconnect(e);
				if (refreshPorts)
				{
					inputNodeView.nodeTarget.UpdateAllPorts();
					inputNodeView.RefreshPorts();
				}
			}
			if (e?.output?.node is BaseNodeView outputNodeView)
			{
				e.output.Disconnect(e);
				if (refreshPorts)
				{
					outputNodeView.nodeTarget.UpdateAllPorts();
					outputNodeView.RefreshPorts();
				}
			}

			edgeViews.Remove(e);
		}

		public void Disconnect(EdgeView e, bool refreshPorts = true)
		{
			DisconnectView(e, refreshPorts);

			// Remove the serialized edge if there is one
			if (e.userData is SerializableEdge serializableEdge)
			{
				graph.Disconnect(serializableEdge.GUID);
				UpdateComputeOrder();
			}
		}

		public void RemoveEdges()
		{
			foreach (var edge in edgeViews)
				RemoveElement(edge);
			edgeViews.Clear();
		}

		public void UpdateComputeOrder()
		{
			graph.UpdateComputeOrder();

			computeOrderUpdated?.Invoke();
		}

		public void RegisterCompleteObjectUndo(string name)
		{
			Undo.RegisterCompleteObjectUndo(graph, name);
		}

		public void SaveGraphToDisk()
		{
			if (graph == null)
				return ;

			EditorUtility.SetDirty(graph);
		}

		public void ToggleView< T >() where T : PinnedElementView
		{
			ToggleView(typeof(T));
		}

		public void ToggleView(Type type)
		{
			PinnedElementView view;
			pinnedElements.TryGetValue(type, out view);

			if (view == null)
				OpenPinned(type);
			else
				ClosePinned(type, view);
		}

		public void OpenPinned< T >() where T : PinnedElementView
		{
			OpenPinned(typeof(T));
		}

		public void OpenPinned(Type type)
		{
			PinnedElementView view;

			if (type == null)
				return ;

			PinnedElement elem = graph.OpenPinned(type);

			if (!pinnedElements.ContainsKey(type))
			{
				view = Activator.CreateInstance(type) as PinnedElementView;
				pinnedElements[type] = view;
				view.InitializeGraphView(elem, this);
			}
			view = pinnedElements[type];

			if (!Contains(view))
				Add(view);
		}

		public void ClosePinned< T >(PinnedElementView view) where T : PinnedElementView
		{
			ClosePinned(typeof(T), view);
		}

		public void ClosePinned(Type type, PinnedElementView elem)
		{
			pinnedElements.Remove(type);
			Remove(elem);
			graph.ClosePinned(type);
		}

		public Status GetPinnedElementStatus< T >() where T : PinnedElementView
		{
			return GetPinnedElementStatus(typeof(T));
		}

		public Status GetPinnedElementStatus(Type type)
		{
			var pinned = graph.pinnedElements.Find(p => p.editorType.type == type);

			if (pinned != null && pinned.opened)
				return Status.Normal;
			else
				return Status.Hidden;
		}

		public void ResetPositionAndZoom()
		{
			graph.position = Vector3.zero;
			graph.scale = Vector3.one;

			UpdateViewTransform(graph.position, graph.scale);
		}

		protected virtual void InitializeView() {}

		public virtual IEnumerable< KeyValuePair< string, Type > > FilterCreateNodeMenuEntries()
		{
			// By default we don't filter anything
			foreach (var nodeMenuItem in NodeProvider.GetNodeMenuEntries())
				yield return nodeMenuItem;

			// TODO: add exposed properties to this list
		}

		#endregion

	}
}
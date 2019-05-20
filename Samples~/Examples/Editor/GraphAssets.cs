﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using GraphProcessor;
using UnityEditor.Callbacks;

public class GraphAssets
{
	[MenuItem("Assets/Create/GraphProcessor", false, 10)]
	public static void CreateGraphPorcessor()
	{
		var		obj = Selection.activeObject;
		string	path;

		if (obj == null)
			path = "Assets";
		else
			path = AssetDatabase.GetAssetPath(obj.GetInstanceID());

		var graph = ScriptableObject.CreateInstance< BaseGraph >();

		ProjectWindowUtil.CreateAsset(graph, path + "/GraphProcessor.asset");
	}

	[OnOpenAsset(0)]
	public static bool OnBaseGraphOpened(int instanceID, int line)
	{
		var obj = EditorUtility.InstanceIDToObject(instanceID);

		if (!(obj is BaseGraph))
			return false;

		var win = BaseGraphWindow.GetWindow< BaseGraphWindow >();
		
		win.Show();

		win.InitializeGraph(obj as BaseGraph);

		return false;
	}

}

﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Microsoft.VisualStudio.R.Controls
{
    public class Variable
    {
        public Variable(VariableView view = null)
        {
            _children = new List<Variable>();

            View = view;

            IsExpanded = false;
        }

        public Variable(Variable parent, VariableView view = null)
            : this(view)
        {
            Parent = parent;
        }

        /// <summary>
        /// <see cref="VariableView"/> that owns this
        /// </summary>
        public VariableView View { get; set; }

        /// <summary>
        /// Name of variable
        /// </summary>
        public string VariableName { get; set; }

        /// <summary>
        /// Value of variable, represented in short string
        /// </summary>
        public string VariableValue { get; set; }

        /// <summary>
        /// Type name of variable
        /// </summary>
        public string TypeName { get; set; }

        bool _isExpanded;
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    if (_isExpanded)
                    {
                        Expand();
                    }
                    else
                    {
                        Collapse();
                    }

                    View?.RefreshView();
                }
            }
        }

        public bool HasChildren
        {
            get
            {
                return (_children != null) && (_children.Count > 0);
            }
        }

        List<Variable> _children;
        public List<Variable> Children
        {
            get
            {
                if (_children == null)
                {
                    _children = new List<Variable>();
                }
                return _children;
            }
        }

        Variable _parent;
        public Variable Parent
        {
            get { return _parent; }
            private set
            {
                _parent = value;
                if (_parent != null)
                {
                    Level = _parent.Level + 1;
                }
            }
        }

        public int Level { get; set; }

        public bool IsVisible { get; set; }

        /// <summary>
        /// simple Depth first traverse of Variable tree, and take action (Recursive)
        /// </summary>
        /// <param name="variables">variables to recurse</param>
        public static void TraverseDepthFirst(IEnumerable<Variable> variables, Func<Variable, bool> action)
        {
            foreach (var variable in variables)
            {
                if (action(variable))
                {
                    if (variable.HasChildren)
                    {
                        TraverseDepthFirst(variable.Children, action);
                    }
                }
            }
        }

        #region Private

        private void Expand()
        {
            TraverseDepthFirst(this.Children,
                (v) => { v.IsVisible = true; return v.IsExpanded; });
        }

        private void Collapse()
        {
            TraverseDepthFirst(this.Children,
                (v) => { v.IsVisible = false; return v.IsExpanded; });
        }

        #endregion
    }
}

﻿(*
Copyright 2011 Stephen Swensen

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*)

module internal Swensen.Unquote.Reduction
open System
open Microsoft.FSharp.Quotations

module P = Microsoft.FSharp.Quotations.Patterns
module DP = Microsoft.FSharp.Quotations.DerivedPatterns
module ES = Microsoft.FSharp.Quotations.ExprShape

open Swensen
module EP = Swensen.Unquote.ExtraPatterns

//hmmm, looking at this with new eyes, having just implemented the Evaluation module,
//i don't like how Vars are considered "reduced" and somewhat related the body of of let bindings is not
//incrementally evaluated.  I think we can have a version of reduce which returns both a reduction
//and an enviroment to be passed into the next reduction... possibly we can have some "evalWithEnvironment (expr:Expr<'a>) (env) -> ('a, env)"
//and "reduceWithEnvironment" functions

///Construct a Value from an evaluated expression
let evalValue env (expr:Expr) = 
    Expr.Value(Evaluation.eval env expr, expr.Type)

//need to keep in synce with the depth of Sprinting.
let rec isReduced = function
    | P.Var v -> false //we need to assume Vars are reducible and let the exceptions fly when env lookup fails, can revist later //env |> List.exists (fun (name,_) -> name = v.Name) |> not //if the variable is in the environment, it can be reduced (return false), otherwise it can't (return true)
    | P.Value _ | P.Lambda _ | DP.Unit -> true
    | P.NewUnionCase(_,args) | P.NewTuple(args) | P.NewArray(_,args) | EP.IncompleteLambdaCall(_,args) when args |> allReduced -> true
    | P.Coerce(arg,_) | P.TupleGet(arg, _) when arg |> isReduced -> true //TupleGet here helps TupleLet expressions reduce correctly
    | _ -> false
and allReduced x = 
    x |> List.forall (isReduced)

// need to handle nested application/lambda expr: replace lambda vars with reduced applications
// unquote <@ ((fun i j -> i + j) 3 4) + 2 @>

//note: we are not super careful about evaluation order (expect, of course, Sequential), which may be an issue.
//reduce all args / calles if any of them are not reduced; otherwise eval
let rec reduce env (expr:Expr) = 
    match expr with
    //if lhs is a Application, PropertyGet, Call, or other unit returning call, may want to discard, rather than deal with null return value.
    | P.Sequential (P.Sequential(lhs, (DP.Unit as u)), rhs) ->
        if lhs |> isReduced then env, rhs
        else
            let env, lhs = reduce env lhs 
            env, Expr.Sequential(Expr.Sequential(lhs, u), rhs)
    | P.Sequential (lhs, rhs) ->
        if lhs |> isReduced then env,rhs
        else
            let env, lhs = reduce env lhs  
            env, Expr.Sequential(lhs, rhs)
    | EP.IncompleteLambdaCall(_,args) when args |> allReduced ->
        env, expr
    | EP.TupleLet(vars, assignment, body) when assignment |> isReduced -> //else defer to ShapeCombination, which will only reduce the assignment and rebuild the Let expression
        let assignment = Evaluation.eval env assignment //a tuple
        let tupleFields = Microsoft.FSharp.Reflection.FSharpValue.GetTupleFields(assignment)
        let env = 
           (Seq.zip vars tupleFields 
            |> Seq.choose
                (fun (var, tupleField) ->
                    match var with
                    | Some(var) -> Some(var.Name, ref tupleField)
                    | None -> None)
            |> Seq.toList) @ env
        reduce env body
    | P.Let(var, assignment, body) when assignment |> isReduced -> //else defer to ShapeCombination, which will only reduce the assignment and rebuild the Let expression
        let env = (var.Name, Evaluation.eval env assignment |> ref)::env
        reduce env body
    | P.Var _ ->
        env, evalValue env expr
//    | DP.Applications(fExpr,args) ->
//        if args |> List.concat |> allReduced then env, evalValue env expr
//        else 
//            let rec reduceFirstNonReduced consumed remaining =
//                match remaining with
//                | h::t when h |> isReduced -> reduceFirstNonReduced (consumed @ [h]) t
//                | h::t -> 
//                    let env, h = reduce env h
//                    env, consumed @ h::t
//            let env, args = reduceFirstNonReduced [] args
//            env, Expr.Applications(fExpr, args)
//    | EP.Range(_,_,a,b) when [a;b] |> allReduced -> //defer to ShapeCombination pattern for rebuilding when not reduced
//        evalValue env expr
//    | EP.RangeStep(_,_,a,b,c) when [a;b;c] |> allReduced -> //defer to ShapeCombination pattern for rebuilding when not reduced
//        evalValue env expr
//    | ES.ShapeVar _ -> expr
//    | ES.ShapeLambda _ -> expr
    | ES.ShapeCombination (o, exprs) -> 
        if isReduced expr then env, expr
        elif allReduced exprs then 
            env, (evalValue env expr)
        else 
            let rec reduceFirstNonReduced consumed remaining =
                match remaining with
                | h::t when h |> isReduced -> reduceFirstNonReduced (consumed @ [h]) t
                | h::t -> 
                    let env, h = reduce env h
                    env, consumed @ h::t
            let env, exprs = reduceFirstNonReduced [] exprs
            env, ES.RebuildShapeCombination(o, exprs)
    
//note Expr uses reference equality and comparison, so have to be
//carefule in reduce algorithm to only rebuild actually reduced parts of an expresion
let reduceFully =
    let rec loop env expr acc =
        try
            let env, nextExpr = expr |> (reduce env)
            if isReduced nextExpr then //is reduced
                if nextExpr <> List.head acc then nextExpr::acc //different than last
                else acc //same as last
            elif nextExpr = List.head acc then //is not reduced and could not reduce
                (evalValue env nextExpr)::acc
            else loop env nextExpr (nextExpr::acc)
        with
        | ex -> 
            Expr.Value(ex)::acc

    fun env expr -> loop env expr [expr] |> List.rev
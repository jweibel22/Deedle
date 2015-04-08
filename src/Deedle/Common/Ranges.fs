﻿// ------------------------------------------------------------------------------------------------
// Ranges - represents a sub-range as a list of pairs of indices
// ------------------------------------------------------------------------------------------------

namespace Deedle.Ranges

open System
open Deedle
open Deedle.Internal
open Deedle.Addressing

type RangeKeyOperations<'T> =
  abstract Compare : 'T * 'T -> int
  abstract IncrementBy : 'T * int64 -> 'T
  abstract Distance : 'T * 'T -> int64
  abstract Range : 'T * 'T -> seq<'T>
  abstract ValidateKey : 'T * Lookup -> OptionalValue<'T>

/// Represents a sub-range of an ordinal index. The range can consist of 
/// multiple blocks, i.e. [ 0..9; 20..29 ]. The pairs represent indices
/// of first and last element (inclusively) and we also keep size so that
/// we do not have to recalculate it.
type Ranges<'T when 'T : equality>(ranges:seq<'T * 'T>, ops:RangeKeyOperations<'T>) = 
  let ranges = Array.ofSeq ranges 
  do
    if ranges.Length = 0 then invalidArg "ranges" "Range cannot be empty"
    for l, h in ranges do 
      if ops.Compare(l, h) > 0 then 
        invalidArg "ranges" "Invalid range (first offset is greater than second)"
  member x.Ranges = ranges
  member x.Operations = ops

module Ranges =  

  /// Create a range from a sequence of low-high pairs
  let inline inlineCreate succ (ranges:seq< ^T * ^T >) =
    let ranges = Array.ofSeq ranges 
    if ranges.Length = 0 then invalidArg "ranges" "Range cannot be empty"
    for l, h in ranges do if l > h then invalidArg "ranges" "Invalid range (first offset is greater than second)"
    let one = LanguagePrimitives.GenericOne
    let ops = 
      { new RangeKeyOperations< ^T > with
          member x.Compare(a, b) = compare a b 
          member x.IncrementBy(a, i) = succ a i
          member x.Distance(l, h) = int64 (h - l)
          member x.Range(a, b) = if a <= b then Seq.range a b else Seq.rangeStep a b (-one) 
          member x.ValidateKey(k, _) = OptionalValue(k) }
    Ranges(ranges, ops)

  /// Create a range from a sequence of low-high pairs
  let inline create (ops:RangeKeyOperations<'T>) (ranges:seq< 'T * 'T >) =
    Ranges(ranges, ops)

  /// Combine ranges - the arguments don't have to be sorted, but must not overlap
  let inline combine(ranges:seq<Ranges<'T>>) : Ranges<'T> =
    if Seq.isEmpty ranges then invalidArg "ranges" "Range cannot be empty"
    let ops = (Seq.head ranges).Operations
    let blocks = 
      [ for r in ranges do yield! r.Ranges ] 
      |> List.sortWith (fun (l1, _) (l2, _) -> ops.Compare(l1, l2))
    let rec loop (startl, endl) blocks = seq {
      match blocks with 
      | [] -> yield startl, endl
      | (s, e)::blocks when ops.Compare(s, endl) <= 0 -> invalidOp "Cannot combine overlapping ranges"
      | (s, e)::blocks when s = ops.IncrementBy(endl, 1L) -> yield! loop (startl, e) blocks
      | (s, e)::blocks -> 
          yield startl, endl
          yield! loop (s, e) blocks }
    create ops (loop (List.head blocks) (List.tail blocks))

  /// Represents invalid address
  let invalid = -1L

  /// Returns the key at a given address. For example, given
  /// ranges [10..12; 30..32], the function defines a mapping:
  ///
  ///   0->10, 1->11, 2->12, 3->30, 4->31, 5->32
  ///
  /// When the address is wrong, throws `IndexOutOfRangeException`
  let keyOfAddress addr (ranges:Ranges<'T>) =
    let rec loop sum idx = 
      if idx >= ranges.Ranges.Length then raise <| IndexOutOfRangeException() else
      let l, h = ranges.Ranges.[idx]

      //let size = ranges.Operations.Distance(l, h)+1L
      //if addr < sum + size then ranges.Operations.IncrementBy(l, addr - sum)

      // Better approach - avoid getting the size of the whole range!
      let incremented = ranges.Operations.IncrementBy(l, addr - sum)
      if ranges.Operations.Compare(incremented, h) <= 0 then incremented
      else 
        let size = ranges.Operations.Distance(l, h)+1L
        loop (sum + size) (idx + 1)

    if addr < 0L then raise <| IndexOutOfRangeException()
    loop 0L 0

  /// Returns the address of a given key. For example, given
  /// ranges [10..12; 30..32], the function defines a mapping:
  ///
  ///   10->0, 11->1, 12->2, 30->3, 31->4, 32->5  
  ///
  /// When the key is wrong, returns `Address.Invalid`
  let addressOfKey key (ranges:Ranges<'T>) =
    let inline (<.) a b = ranges.Operations.Compare(a, b) < 0
    let inline (>=.) a b = ranges.Operations.Compare(a, b) >= 0
    let inline (<=.) a b = ranges.Operations.Compare(a, b) <= 0

    let rec loop addr idx = 
      if idx >= ranges.Ranges.Length then invalid else
      let l, h = ranges.Ranges.[idx]
      if key <. l then invalid
      elif key >=. l && key <=. h then addr + ranges.Operations.Distance(l, key)
      else loop (addr + ranges.Operations.Distance(l, h) + 1L) (idx + 1)
    
    if ranges.Operations.ValidateKey(key, Lookup.Exact).HasValue
    then loop 0L 0 else invalid

  // Restriction has indices as local addresses 
  let restrictRanges restriction (ranges:Ranges<'T>) =
    let inline (<.) a b = ranges.Operations.Compare(a, b) < 0
    let inline (>.) a b = ranges.Operations.Compare(a, b) > 0
    let inline (>=.) a b = ranges.Operations.Compare(a, b) >= 0
    let inline (<=.) a b = ranges.Operations.Compare(a, b) <= 0
    let inline max a b = if a >=. b then a else b
    let inline min a b = if a <=. b then a else b

    match restriction with
    | RangeRestriction.Fixed(loRestr, hiRestr) ->
        // Restrict the current ranges to a range specified by lower and higher address
        let newRanges = 
          [| for lo, hi in ranges.Ranges do
               if lo >=. loRestr && hi <=. hiRestr then yield lo, hi       
               elif hi <. loRestr || lo >. hiRestr then ()
               else yield max lo loRestr, min hi hiRestr |]
        Ranges(newRanges, ranges.Operations)
    
    | Let (true, +1) ((isStart, step), RangeRestriction.Start(desiredCount)) 
    | Let (false,-1) ((isStart, step), RangeRestriction.End(desiredCount)) ->
        let rec loop rangeIdx desiredCount = seq {
          if desiredCount > 0 then
            if rangeIdx < 0 || rangeIdx >= ranges.Ranges.Length then
              invalidOp "Insufficient number of elements in the range"
            let lo, hi = ranges.Ranges.[rangeIdx]
            let range = if isStart then ranges.Operations.Range(lo, hi) else ranges.Operations.Range(hi, lo)
            let last, length = range |> Seq.truncate desiredCount |> Seq.lastAndLength
            yield if isStart then lo, last else last, hi
            yield! loop (rangeIdx + step) (desiredCount - length) }
        
        if isStart then Ranges(loop 0 (int desiredCount) |> Array.ofSeq, ranges.Operations)
        else Ranges(loop (ranges.Ranges.Length-1) (int desiredCount) |> Array.ofSeq |> Array.rev, ranges.Operations)

    | _ -> failwith "restrictRanges: TODO"
      
  /// Returns the smallest & greatest overall key
  let inline keyRange (ranges:Ranges<'T>) =
    fst ranges.Ranges.[0], snd ranges.Ranges.[ranges.Ranges.Length-1]

  /// Returns a lazy sequence containing all keys
  let inline keys (ranges:Ranges<_>) =
    seq { for l, h in ranges.Ranges do yield! ranges.Operations.Range(l, h) }

  /// Returns the length of the ranges
  let inline length (ranges:Ranges<_>) =
    ranges.Ranges |> Array.sumBy (fun (l, h) -> 
      ranges.Operations.Distance(l, h)+1L)

  /// Implements a lookup using the specified semantics & check function.
  /// For `Exact` match, this is the same as `addressOfKey`. In other cases,
  /// we first find the place where the key *would* be and then scan in one
  /// or the other direction until 'check' returns 'true' or we find the end.
  let lookup key semantics (check:'T -> int64 -> bool) (ranges:Ranges<'T>) =
    let inline (<.) a b = ranges.Operations.Compare(a, b) < 0
    let inline (<.) a b = ranges.Operations.Compare(a, b) < 0
    let inline (>=.) a b = ranges.Operations.Compare(a, b) >= 0
    let inline (<=.) a b = ranges.Operations.Compare(a, b) <= 0

    if semantics = Lookup.Exact then
      // For exact lookup, we only check one address
      let addr = addressOfKey key ranges
      if addr <> invalid && check key addr then
        OptionalValue( (key, addr) )
      else OptionalValue.Missing
    else
      // Validate the key - returns key that is valid value 
      let keyOpt = ranges.Operations.ValidateKey(key, semantics) 
      if keyOpt = OptionalValue.Missing then OptionalValue.Missing else 
      let validKey = keyOpt.Value

      // Otherwise, we scan next addresses in the required direction
      let step = 
        if semantics &&& Lookup.Greater = Lookup.Greater then (+) 1L
        elif semantics &&& Lookup.Smaller = Lookup.Smaller then (+) -1L
        else invalidArg "semantics" "Invalid lookup semantics (1)"

      // Find start
      let start, rangeIdx =
        let rec loop addr idx = 
          if idx >= ranges.Ranges.Length && (semantics &&& Lookup.Greater = Lookup.Greater) then invalid, -1
          elif idx >= ranges.Ranges.Length && (semantics &&& Lookup.Smaller = Lookup.Smaller) then addr-1L, ranges.Ranges.Length-1 
          else
            let l, h = ranges.Ranges.[idx]
            if validKey >=. l && validKey <=. h then addr + ranges.Operations.Distance(l, validKey), idx
            elif validKey <. l && (semantics &&& Lookup.Greater = Lookup.Greater) then addr, idx
            elif validKey <. l && (semantics &&& Lookup.Smaller = Lookup.Smaller) then addr - 1L, idx - 1
            else loop (addr + ranges.Operations.Distance(l, h) + 1L) (idx + 1)
        loop 0L 0

      if start = invalid || rangeIdx = -1 then OptionalValue.Missing else
      let keyStart = keyOfAddress start ranges

      // Scan until 'check' returns true, or until we reach invalid address
      let keysToScan = 
        if semantics = Lookup.Exact then seq { yield keyStart }
        elif semantics &&& Lookup.Greater = Lookup.Greater then
          seq { yield! ranges.Operations.Range(keyStart, snd ranges.Ranges.[rangeIdx])
                for lo, hi in ranges.Ranges.[rangeIdx + 1 ..] do
                  yield! ranges.Operations.Range(lo, hi) }
        elif semantics &&& Lookup.Smaller = Lookup.Smaller then
          seq { yield! ranges.Operations.Range(keyStart, fst ranges.Ranges.[rangeIdx])
                for lo, hi in ranges.Ranges.[.. rangeIdx-1] |> Array.rev do
                  yield! ranges.Operations.Range(hi, lo) }
        else invalidArg "semantics" "Invalid lookup semantics"

      // Skip one if needed
      Seq.zip keysToScan (Seq.unreduce step start)
      |>  ( if keyStart = key && (semantics = Lookup.Greater || semantics = Lookup.Smaller) 
            then Seq.skip 1 else id )
      |> Seq.tryFind (fun (k, a) -> check k a)
      |> OptionalValue.ofOption

type Ranges<'T> with
  member x.FirstKey = fst (Ranges.keyRange x)
  member x.LastKey = snd (Ranges.keyRange x)
  member x.KeyAtOffset(offset) = Ranges.keyOfAddress offset x
  member x.OffsetOfKey(key) = 
    let res = Ranges.addressOfKey key x
    if res = Ranges.invalid then raise <| IndexOutOfRangeException()
    else res
  member x.Keys = Ranges.keys x
  member x.Length = Ranges.length x
  member x.Lookup(key, semantics, check:Func<_, _, _>) = 
    Ranges.lookup key semantics (fun a b -> check.Invoke(a, b)) x
  member x.MergeWith(ranges) = 
    Ranges.combine [yield x; yield! ranges]
  member x.Restrict(restriction) = 
    Ranges.restrictRanges restriction x
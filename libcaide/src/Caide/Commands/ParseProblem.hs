{-# LANGUAGE OverloadedStrings #-}
module Caide.Commands.ParseProblem(
      createProblem
    , parseExistingProblem
) where

import Control.Monad (forM_, when)
import Control.Monad.State (liftIO)
import Data.Char (isAlphaNum, isAscii)
import qualified Data.Text as T
import qualified Data.Text.IO as T

import Filesystem (createDirectory, createTree, writeTextFile, isDirectory)
import Filesystem.Path.CurrentOS (fromText, decodeString, (</>))

import Caide.Types
import Caide.Configuration (getDefaultLanguage, setActiveProblem, getProblemConfigFile, getProblemStateFile, defaultProblemConfig, defaultProblemState)
import Caide.Commands.BuildScaffold (generateScaffoldSolution)
import Caide.Commands.Make (updateTests)
import Caide.Registry (findProblemParser)
import Caide.Util (pathToText)



createProblem :: URL -> T.Text -> CaideIO ()
createProblem url problemTypeStr = case findProblemParser url of
    Just parser -> parseExistingProblem url parser
    Nothing     -> case optionFromString (T.unpack problemTypeStr) of
        Nothing    -> throw . T.concat $ ["Incorrect problem type: ", problemTypeStr]
        Just pType -> createNewProblem url pType

initializeProblem :: Problem -> CaideIO ()
initializeProblem problem = do
    root <- caideRoot
    let probId = problemId problem
        testDir = root </> fromText probId </> ".caideproblem" </> "test"
    problemConfPath  <- getProblemConfigFile probId
    problemStatePath <- getProblemStateFile probId

    liftIO $ createTree testDir

    hProblemConf <- createConf problemConfPath defaultProblemConfig
    setProp hProblemConf "problem" "name" $ problemName problem
    setProp hProblemConf "problem" "type" $ problemType problem
    hProblemState <- createConf problemStatePath defaultProblemState

    flushConf hProblemConf
    flushConf hProblemState

    lang <- getDefaultLanguage
    updateTests
    generateScaffoldSolution lang


createNewProblem :: ProblemID -> ProblemType -> CaideIO ()
createNewProblem probId probType = do
    when (T.any (\c -> not (isAscii c) || not (isAlphaNum c)) probId) $
        throw . T.concat $ [probId, " is not recognized as a supported URL. ",
            "To create an empty problem, input a valid problem ID (a string of alphanumeric characters)"]

    root <- caideRoot
    let problemDir = root </> fromText probId
        problem = Problem
            { problemId = probId
            , problemName = probId
            , problemType = probType
            }

    -- Prepare problem directory
    liftIO $ createDirectory False problemDir


    -- Set active problem
    setActiveProblem probId
    initializeProblem problem
    liftIO $ T.putStrLn . T.concat $ ["Problem successfully created in folder ", probId]


parseExistingProblem :: URL -> ProblemParser -> CaideIO ()
parseExistingProblem url parser = do
    parseResult <- liftIO $ parser `parseProblem` url
    case parseResult of
        Left err -> throw . T.unlines $ ["Encountered a problem while parsing:", err]
        Right (problem, samples) -> do
            root <- caideRoot

            let probId = problemId problem
                problemDir = root </> fromText probId

            problemDirExists <- liftIO $ isDirectory problemDir
            when problemDirExists $
                throw . T.concat $ ["Problem directory already exists: ", pathToText problemDir]

            liftIO $ do
                -- Prepare problem directory
                createDirectory False problemDir

                -- Write test cases
                forM_ (zip samples [1::Int ..]) $ \(sample, i) -> do
                    let inFile  = problemDir </> decodeString ("case" ++ show i ++ ".in")
                        outFile = problemDir </> decodeString ("case" ++ show i ++ ".out")
                    writeTextFile inFile  $ testCaseInput sample
                    writeTextFile outFile $ testCaseOutput sample


            -- Set active problem
            setActiveProblem probId
            initializeProblem problem
            liftIO $ T.putStrLn . T.concat $ ["Problem successfully parsed into folder ", probId]


